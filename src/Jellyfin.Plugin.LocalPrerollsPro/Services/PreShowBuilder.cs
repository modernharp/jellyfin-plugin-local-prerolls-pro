using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

public sealed class PlannedIntro
{
    public string Key { get; init; }

    public string Name { get; init; }

    /// <summary>File that is streamed: the original, or its converted / volume-matched copy.</summary>
    public string Path { get; init; }

    public Guid ItemId { get; init; }

    /// <summary>Which step added it: Title, Rules, Fixed or Trailers.</summary>
    public string Source { get; init; }

    /// <summary>True when a converted or volume-matched copy is played instead of the original.</summary>
    public bool Prepared { get; init; }
}

public sealed class StepReport
{
    public int Index { get; init; }

    public string Kind { get; init; }

    public string Note { get; set; }

    public SelectionResult Selection { get; set; }

    public List<string> Picked { get; } = new();
}

public sealed class PreShowPlan
{
    public List<PlannedIntro> Intros { get; } = new();

    /// <summary>Why no intros play, when a gate stopped them.</summary>
    public string SkipReason { get; set; }

    public List<StepReport> Steps { get; } = new();

    public FeatureContext Context { get; set; }

    /// <summary>Intros left out before the rules ran (turned off, out of schedule, rated too high).</summary>
    public Dictionary<string, string> Excluded { get; set; } = new();

    /// <summary>Names of intros found in the title's own folder.</summary>
    public List<string> TitleIntros { get; } = new();
}

/// <summary>
/// Decides the full list of intros to play before a feature: checks the gates
/// (opt-outs, feature type, user, cooldown, episode mode), then runs each pre-show step in order.
/// </summary>
public sealed class PreShowBuilder
{
    private const string TitleKeyPrefix = "title:";

    private readonly IntroCatalog _catalog;
    private readonly PlaybackHistory _history;
    private readonly FeatureContextBuilder _contextBuilder;
    private readonly IntroPreparer _preparer;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PreShowBuilder> _logger;

    public PreShowBuilder(IntroCatalog catalog, PlaybackHistory history, FeatureContextBuilder contextBuilder, IntroPreparer preparer, ILibraryManager libraryManager, ILogger<PreShowBuilder> logger)
    {
        _catalog = catalog;
        _history = history;
        _contextBuilder = contextBuilder;
        _preparer = preparer;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <param name="item">The feature.</param>
    /// <param name="user">The viewer (may be null).</param>
    /// <param name="nowLocal">Server local time used for date and time rules.</param>
    /// <param name="record">False for the test tool: nothing is remembered.</param>
    public PreShowPlan Build(BaseItem item, User user, DateTime nowLocal, bool record)
    {
        var plan = new PreShowPlan();
        var config = PrerollsPlugin.Instance.Configuration;
        var utcNow = nowLocal.ToUniversalTime();
        var userId = user?.Id.ToString("N") ?? string.Empty;

        if (config.SchemaVersion < PluginConfiguration.CurrentSchemaVersion)
        {
            plan.SkipReason = "Settings are still being set up. Try again in a moment.";
            return plan;
        }

        if (IntroCatalog.IsIntroItem(item))
        {
            plan.SkipReason = "This video is itself a pre-roll.";
            return plan;
        }

        var kind = item.GetBaseItemKind().ToString();
        if (!config.EnabledItemTypes.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            plan.SkipReason = $"Pre-rolls are turned off for {KindName(kind)} (Playback tab, \"Play pre-rolls before\").";
            return plan;
        }

        var optOut = OptOutReason(item, config);
        if (optOut != null)
        {
            plan.SkipReason = optOut;
            return plan;
        }

        if (user != null && config.ExcludedUserIds.Select(ConditionMatcher.NormalizeId).Contains(userId))
        {
            plan.SkipReason = $"Pre-rolls are turned off for user {user.Username} (Playback tab, \"Viewers who never get pre-rolls\").";
            return plan;
        }

        if (config.MinMinutesBetweenIntros > 0 && _history.LastIntroUtc(userId) is { } last
            && utcNow - last < TimeSpan.FromMinutes(config.MinMinutesBetweenIntros))
        {
            plan.SkipReason = $"This viewer had a pre-show {(int)(utcNow - last).TotalMinutes} min ago, and the Playback tab allows one every {config.MinMinutesBetweenIntros} min.";
            return plan;
        }

        var ctx = _contextBuilder.Build(item, user, nowLocal, config.Rules);
        plan.Context = ctx;

        if (kind == nameof(BaseItemKind.Episode) && !string.IsNullOrEmpty(ctx.SeriesId))
        {
            var skip = EpisodeGate(config, userId, ctx.SeriesId, nowLocal, utcNow);
            if (record)
            {
                _history.RecordSeriesActivity(userId, ctx.SeriesId, utcNow);
            }

            if (skip != null)
            {
                plan.SkipReason = skip;
                return plan;
            }
        }

        // Per-intro checks: on/off, schedule, age rating.
        var options = new EligibilityOptions
        {
            AgeSafe = config.AgeSafe,
            UnratedTitlesAllowAll = config.AgeSafeUnratedAllowAll,
            BoostNewDays = config.BoostNewDays,
            BoostNewFactor = config.BoostNewFactor,
        };
        var metaCache = new Dictionary<string, IntroMeta>(StringComparer.OrdinalIgnoreCase);
        IntroMeta Meta(string key) => metaCache.TryGetValue(key, out var m) ? m : metaCache[key] = _catalog.Meta(key);

        var eligibility = IntroEligibility.Filter(_catalog.Files.Keys, Meta, ctx, options);
        plan.Excluded = eligibility.Excluded;
        var available = eligibility.Eligible;

        // Intros from the title's own ".prerolls" folder.
        var titleIntros = config.TitleIntrosEnabled ? FindTitleIntros(item, config) : new List<(string Key, string Path, string Name)>();
        plan.TitleIntros.AddRange(titleIntros.Select(t => t.Name));
        var titleByKey = titleIntros.ToDictionary(t => t.Key, t => t, StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recent = _history.RecentKeys(userId, config.AvoidRepeatCount);
        var cursorUpdates = new List<KeyValuePair<string, string>>();

        if (titleIntros.Count > 0 && config.TitleIntrosMode == TitleIntroModes.AddFirst)
        {
            var report = new StepReport { Index = 0, Kind = "Title", Note = $"From the title's {config.TitleFolderName} folder." };
            plan.Steps.Add(report);
            foreach (var t in titleIntros)
            {
                used.Add(t.Key);
                AddTitle(plan, report, t);
            }
        }

        var steps = config.PreShow.Count > 0 ? config.PreShow : new List<PreShowStep> { new() };

        // Pre-rolls of Fixed steps are kept for those steps, so a rule pick never takes a bumper
        // and plays it in the wrong place.
        var reserved = new HashSet<string>(
            steps.Where(s => s.Enabled && s.Kind == PreShowStepKinds.Fixed && (s.ApplyTo.Count == 0 || s.ApplyTo.Contains(kind, StringComparer.OrdinalIgnoreCase)))
                .SelectMany(s => IntroSelector.ResolveIntros(s.Intros, available, k => Meta(k)?.Tags)),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var report = new StepReport { Index = i + 1, Kind = step.Kind };
            plan.Steps.Add(report);

            if (!step.Enabled)
            {
                report.Note = "Step is turned off.";
                continue;
            }

            if (step.ApplyTo.Count > 0 && !step.ApplyTo.Contains(kind, StringComparer.OrdinalIgnoreCase))
            {
                report.Note = $"Step does not apply to {kind} items.";
                continue;
            }

            switch (step.Kind)
            {
                case PreShowStepKinds.Fixed:
                    foreach (var key in IntroSelector.ResolveIntros(step.Intros, available, k => Meta(k)?.Tags))
                    {
                        if (used.Add(key))
                        {
                            Add(plan, report, key, PreShowStepKinds.Fixed);
                        }
                    }

                    if (report.Picked.Count == 0)
                    {
                        report.Note = "None of this step's pre-rolls can play right now (see \"Left out before the rules ran\").";
                    }

                    break;

                case PreShowStepKinds.Trailers:
                    AddTrailers(plan, report, item, user, ctx, Math.Clamp(step.Count, 0, 10), step.SameGenre);
                    break;

                default:
                    var group = step.Group ?? string.Empty;
                    var useTitle = titleIntros.Count > 0 && config.TitleIntrosMode == TitleIntroModes.Replace && group.Length == 0;
                    var rules = useTitle
                        ? new List<IntroRule> { new() { Id = "title", Name = $"This title's {config.TitleFolderName} folder", Precedence = int.MaxValue, Intros = titleIntros.Select(t => t.Key).ToList() } }
                        : config.Rules.Where(r => string.Equals(r.Group ?? string.Empty, group, StringComparison.OrdinalIgnoreCase)).ToList();
                    var selection = IntroSelector.Select(
                        new SelectionRequest
                        {
                            Rules = rules,
                            AvailableKeys = useTitle ? titleIntros.Select(t => t.Key).ToList() : available,
                            Count = Math.Clamp(step.Count, 0, 10),
                            Mode = config.SelectionMode,
                            RecentKeys = recent,
                            ExcludeKeys = new HashSet<string>(used.Concat(reserved), StringComparer.OrdinalIgnoreCase),
                            PlayCount = _history.PlayCount,
                            GetCursor = _history.GetCursor,
                            TagsOf = k => Meta(k)?.Tags,
                            IntroWeight = k => IntroEligibility.WeightFactor(Meta(k), utcNow, options),
                        },
                        ctx);
                    report.Selection = selection;
                    if (useTitle)
                    {
                        report.Note = $"This title has its own pre-rolls ({config.TitleFolderName} folder), so they are used instead of the rules.";
                    }

                    cursorUpdates.AddRange(selection.CursorUpdates);
                    foreach (var key in selection.Picks)
                    {
                        used.Add(key);
                        if (titleByKey.TryGetValue(key, out var t))
                        {
                            AddTitle(plan, report, t);
                        }
                        else
                        {
                            Add(plan, report, key, PreShowStepKinds.Rules);
                        }
                    }

                    if (selection.Picks.Count == 0 && report.Note == null)
                    {
                        report.Note = rules.Count == 0
                            ? (group.Length == 0 ? "No rules without a group." : $"No rules in group \"{group}\".")
                            : "No rule matched this title, or the pre-rolls of the matching rules are all left out.";
                    }

                    break;
            }
        }

        if (plan.Intros.Count == 0 && plan.SkipReason == null)
        {
            plan.SkipReason = _catalog.Files.Count == 0 && titleIntros.Count == 0
                ? "No pre-roll files found. Check the folder on the Files tab and run a scan."
                : "No step produced a pre-roll for this title. See the steps below for why.";
        }

        if (record)
        {
            var keys = plan.Intros.Where(p => p.Key != null).Select(p => p.Key).ToList();
            _history.RecordPicks(userId, keys, ctx.SeriesId, cursorUpdates, utcNow);
            _logger.LogDebug("Intros for {Item}: {Intros}", item.Name, string.Join(", ", plan.Intros.Select(p => p.Name)));
        }

        return plan;
    }

    private static string KindName(string kind) => kind switch
    {
        "Movie" => "movies",
        "Episode" => "TV episodes",
        "MusicVideo" => "music videos",
        "Video" => "other videos",
        _ => kind + " items",
    };

    private string OptOutReason(BaseItem item, PluginConfiguration config)
    {
        var chain = new List<BaseItem> { item };
        if (item is Episode episode)
        {
            chain.Add(Safe(() => episode.Season));
            chain.Add(Safe(() => episode.Series));
        }
        else if (item is Season season)
        {
            chain.Add(Safe(() => season.Series));
        }

        chain = chain.Where(x => x != null).ToList();
        var optOutIds = config.OptOutItemIds.Select(ConditionMatcher.NormalizeId).ToHashSet();
        var hit = chain.FirstOrDefault(x => optOutIds.Contains(x.Id.ToString("N")));
        if (hit != null)
        {
            return $"\"{hit.Name}\" is on the list of titles that never get pre-rolls (Playback tab).";
        }

        var tag = config.OptOutTag?.Trim();
        if (!string.IsNullOrEmpty(tag))
        {
            hit = chain.FirstOrDefault(x => (x.Tags ?? Array.Empty<string>()).Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (hit != null)
            {
                return $"\"{hit.Name}\" has the tag \"{tag}\", so it never gets pre-rolls.";
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for a folder named like ".prerolls" next to the title: in the movie folder,
    /// or for an episode in its season folder, then its series folder.
    /// </summary>
    private List<(string Key, string Path, string Name)> FindTitleIntros(BaseItem item, PluginConfiguration config)
    {
        var result = new List<(string Key, string Path, string Name)>();
        var name = string.IsNullOrWhiteSpace(config.TitleFolderName) ? ".prerolls" : config.TitleFolderName.Trim();
        var extensions = IntroKeys.ParseExtensions(config.VideoExtensions);

        var folders = new List<string>();
        if (item is Episode episode)
        {
            folders.Add(Safe(() => episode.Season)?.Path);
            folders.Add(Safe(() => episode.Series)?.Path);
        }
        else
        {
            folders.Add(item.IsFolder ? item.Path : Safe(() => item.ContainingFolderPath));
        }

        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)))
        {
            var dir = Path.Combine(folder, name);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(dir).Where(p => extensions.Contains(Path.GetExtension(p)) && !Path.GetFileName(p).StartsWith('.')).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add((TitleKeyPrefix + path, path, IntroKeys.DisplayName(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Cannot read title intro folder {Dir}: {Message}", dir, ex.Message);
            }

            if (result.Count > 0)
            {
                break; // The closest folder wins (season before series).
            }
        }

        return result;
    }

    private string EpisodeGate(PluginConfiguration config, string userId, string seriesId, DateTime nowLocal, DateTime utcNow)
    {
        switch (config.EpisodeMode)
        {
            case EpisodeModes.FirstOfBinge:
                if (_history.SeriesActivityUtc(userId, seriesId) is { } activity
                    && utcNow - activity < TimeSpan.FromMinutes(Math.Max(1, config.BingeGapMinutes)))
                {
                    return $"TV episodes are set to \"only the first episode of a binge\", and an episode of this series was watched {(int)(utcNow - activity).TotalMinutes} min ago.";
                }

                return null;
            case EpisodeModes.OncePerSeriesPerDay:
                if (_history.SeriesIntroUtc(userId, seriesId) is { } given && given.ToLocalTime().Date == nowLocal.Date)
                {
                    return "TV episodes are set to \"once per series per day\", and this series already had a pre-show today.";
                }

                return null;
            default:
                return null;
        }
    }

    private void Add(PreShowPlan plan, StepReport report, string key, string source)
    {
        if (!_catalog.Files.TryGetValue(key, out var file))
        {
            return;
        }

        var target = _preparer.PlaybackTarget(file);
        var name = _catalog.Meta(key)?.Name ?? file.Name;
        plan.Intros.Add(new PlannedIntro { Key = key, Name = name, Path = target.Path, ItemId = target.ItemId, Source = source, Prepared = target.Prepared });
        report.Picked.Add(name + (target.Prepared ? " (prepared copy)" : string.Empty));
    }

    private void AddTitle(PreShowPlan plan, StepReport report, (string Key, string Path, string Name) t)
    {
        var id = _catalog.EnsureItem(t.Path);
        plan.Intros.Add(new PlannedIntro { Key = t.Key, Name = t.Name, Path = t.Path, ItemId = id, Source = "Title" });
        report.Picked.Add(t.Name);
    }

    /// <summary>Adds local trailers of random movies the user has not watched.</summary>
    private void AddTrailers(PreShowPlan plan, StepReport report, BaseItem feature, User user, FeatureContext ctx, int count, bool sameGenre)
    {
        if (count == 0)
        {
            return;
        }

        if (user == null)
        {
            report.Note = "Trailers need a viewer (to know what is unwatched).";
            return;
        }

        var seen = new HashSet<Guid> { feature.Id };
        IEnumerable<BaseItem> Candidates(bool byGenre)
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsPlayed = false,
                Recursive = true,
                Limit = 60,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            };
            if (byGenre)
            {
                query.Genres = ctx.Genres.ToList();
            }

            return _libraryManager.GetItemList(query);
        }

        var pools = sameGenre && ctx.Genres.Count > 0 ? new[] { true, false } : new[] { false };
        foreach (var byGenre in pools)
        {
            foreach (var movie in Candidates(byGenre))
            {
                if (report.Picked.Count >= count)
                {
                    return;
                }

                if (!seen.Add(movie.Id))
                {
                    continue;
                }

                var trailer = movie.GetExtras(new[] { ExtraType.Trailer }, user).FirstOrDefault(t => !string.IsNullOrEmpty(t.Path));
                if (trailer == null)
                {
                    continue;
                }

                plan.Intros.Add(new PlannedIntro { Name = $"Trailer: {movie.Name}", Path = trailer.Path, ItemId = trailer.Id, Source = PreShowStepKinds.Trailers });
                report.Picked.Add($"Trailer: {movie.Name}");
            }
        }

        if (report.Picked.Count == 0)
        {
            report.Note = "No unwatched movie with a local trailer was found.";
        }
    }

    private T Safe<T>(Func<T> f)
        where T : class
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read item details");
            return null;
        }
    }
}
