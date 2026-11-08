using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;

namespace Jellyfin.Plugin.LocalPrerollsPro.Configuration;

/// <summary>
/// Last line of defence for saved settings: anything out of range or malformed is clamped or reset,
/// whether it came from the settings page, a restored backup, or a hand-edited file.
/// The settings page validates first and explains problems; this only guarantees safe values.
/// </summary>
public static class ConfigSanitizer
{
    public static readonly string[] ItemTypes = { "Movie", "Episode", "MusicVideo", "Video" };
    public static readonly string[] Platforms = { string.Empty, "jellyfin", "plex", "emby" };
    private static readonly Regex ExtensionPattern = new("^\\.[a-z0-9]{1,6}$", RegexOptions.Compiled);

    public const int MaxAvoidRepeat = 25;
    public const int MaxCooldownMinutes = 10080; // a week
    public const int MaxBingeGapMinutes = 1440;
    public const int MaxBoostDays = 365;
    public const double MaxBoostFactor = 10;
    public const double MinLoudness = -36;
    public const double MaxLoudness = -10;
    public const int MaxStepCount = 10;
    public const int MaxPriority = 1000;
    public const int MaxWeight = 1000;

    /// <summary>Fixes the settings in place. Returns a description of each change made.</summary>
    public static List<string> Sanitize(PluginConfiguration c)
    {
        var fixes = new List<string>();

        c.IntroFolders = (c.IntroFolders ?? new())
            .Select(f => (f ?? string.Empty).Trim())
            .Where(f => f.Length > 0)
            .Select(f => f.Length > 1 ? f.TrimEnd('/', '\\') : f)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var extensions = NormalizeExtensions(c.VideoExtensions);
        if (extensions.Length == 0)
        {
            fixes.Add("No valid video file types; using the defaults.");
            extensions = PluginConfiguration.DefaultVideoExtensions;
        }

        c.VideoExtensions = extensions;
        c.SelectionMode = OneOf(c.SelectionMode, new[] { SelectionModes.WeightedRandom, SelectionModes.LeastPlayed, SelectionModes.Sequential }, SelectionModes.WeightedRandom, "selection mode", fixes);
        c.EpisodeMode = OneOf(c.EpisodeMode, new[] { EpisodeModes.EveryEpisode, EpisodeModes.FirstOfBinge, EpisodeModes.OncePerSeriesPerDay }, EpisodeModes.EveryEpisode, "episode mode", fixes);
        c.TitleIntrosMode = OneOf(c.TitleIntrosMode, new[] { TitleIntroModes.Replace, TitleIntroModes.AddFirst }, TitleIntroModes.Replace, "title pre-roll mode", fixes);

        c.AvoidRepeatCount = Clamp(c.AvoidRepeatCount, 0, MaxAvoidRepeat, "avoid repeats", fixes);
        c.MinMinutesBetweenIntros = Clamp(c.MinMinutesBetweenIntros, 0, MaxCooldownMinutes, "minutes between pre-shows", fixes);
        c.BingeGapMinutes = Clamp(c.BingeGapMinutes, 1, MaxBingeGapMinutes, "binge break", fixes);
        c.BoostNewDays = Clamp(c.BoostNewDays, 0, MaxBoostDays, "boost days", fixes);
        c.BoostNewFactor = Clamp(c.BoostNewFactor, 1, MaxBoostFactor, "boost factor", fixes);
        c.TargetLoudness = Clamp(c.TargetLoudness, MinLoudness, MaxLoudness, "target loudness", fixes);

        if (!IsSafeFolderName(c.TitleFolderName))
        {
            fixes.Add($"Per-title folder name \"{c.TitleFolderName}\" is not a plain folder name; using \".prerolls\".");
            c.TitleFolderName = ".prerolls";
        }
        else
        {
            c.TitleFolderName = c.TitleFolderName.Trim();
        }

        c.OptOutTag = Truncate((c.OptOutTag ?? string.Empty).Trim(), 100);
        c.EnabledItemTypes = (c.EnabledItemTypes ?? new()).Where(t => ItemTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Select(t => ItemTypes.First(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        c.ExcludedUserIds = Guids(c.ExcludedUserIds);
        c.OptOutItemIds = Guids(c.OptOutItemIds);

        var ids = new HashSet<string>();
        c.Rules = (c.Rules ?? new()).Where(r => r != null).ToList();
        foreach (var r in c.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id))
            {
                r.Id = Guid.NewGuid().ToString("N");
                ids.Add(r.Id);
            }

            r.Name = Truncate((r.Name ?? string.Empty).Trim(), 100);
            r.Group = Truncate((r.Group ?? string.Empty).Trim(), 50);
            r.Precedence = Math.Clamp(r.Precedence, -MaxPriority, MaxPriority);
            r.Weight = Math.Clamp(r.Weight, 1, MaxWeight);
            r.MatchMode = string.Equals(r.MatchMode, "Any", StringComparison.OrdinalIgnoreCase) ? "Any" : "All";
            r.Intros = CleanList(r.Intros);
            r.Conditions = (r.Conditions ?? new())
                .Where(x => x != null && ConditionKinds.All.Contains(x.Kind))
                .Select(x =>
                {
                    x.Values = CleanList(x.Values);
                    x.From = (x.From ?? string.Empty).Trim();
                    x.To = (x.To ?? string.Empty).Trim();
                    return x;
                })
                .ToList();
        }

        c.PreShow = (c.PreShow ?? new()).Where(s => s != null).ToList();
        foreach (var s in c.PreShow)
        {
            s.Kind = OneOf(s.Kind, new[] { PreShowStepKinds.Rules, PreShowStepKinds.Fixed, PreShowStepKinds.Trailers }, PreShowStepKinds.Rules, "pre-show step", fixes);
            s.Count = Math.Clamp(s.Count, 1, MaxStepCount);
            s.Group = Truncate((s.Group ?? string.Empty).Trim(), 50);
            s.Intros = CleanList(s.Intros);
            s.ApplyTo = (s.ApplyTo ?? new()).Where(t => ItemTypes.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        c.FileSettings = (c.FileSettings ?? new())
            .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Key))
            .GroupBy(f => f.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var f = g.Last();
                f.Key = g.Key;
                f.Name = string.IsNullOrWhiteSpace(f.Name) ? null : Truncate(f.Name.Trim(), 100);
                f.Tags = CleanList(f.Tags);
                f.Weight = f.Weight is > 0 ? Math.Clamp(f.Weight.Value, 0.1, 100) : null;
                f.Rating = string.IsNullOrWhiteSpace(f.Rating) ? null : f.Rating.Trim();
                f.From = string.IsNullOrWhiteSpace(f.From) ? null : f.From.Trim();
                f.To = string.IsNullOrWhiteSpace(f.To) ? null : f.To.Trim();
                return f;
            })
            .ToList();

        c.CatalogDownloadFolder = (c.CatalogDownloadFolder ?? string.Empty).Trim();
        c.CatalogSubscriptions = (c.CatalogSubscriptions ?? new())
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Value))
            .Select(s =>
            {
                s.Kind = string.Equals(s.Kind, "tag", StringComparison.OrdinalIgnoreCase) ? "tag" : "category";
                s.Value = s.Value.Trim().ToLowerInvariant();
                s.Platform = Platforms.Contains((s.Platform ?? string.Empty).Trim().ToLowerInvariant()) ? (s.Platform ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
                return s;
            })
            .GroupBy(s => s.Kind + "|" + s.Value + "|" + s.Platform)
            .Select(g => g.First())
            .ToList();

        return fixes;
    }

    /// <summary>"mp4, MKV;.webm" -> ".mp4,.mkv,.webm". Drops anything that isn't a short alphanumeric extension.</summary>
    public static string NormalizeExtensions(string text) =>
        string.Join(",", IntroKeys.ParseExtensions(text).Select(e => e.ToLowerInvariant()).Where(e => ExtensionPattern.IsMatch(e)).Distinct());

    /// <summary>True for a plain folder name such as ".prerolls": no slashes, not "." or "..", no invalid characters.</summary>
    public static bool IsSafeFolderName(string name)
    {
        var n = (name ?? string.Empty).Trim();
        return n.Length is > 0 and <= 100 && n != "." && n != ".."
            && n.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0
            && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string OneOf(string value, string[] allowed, string fallback, string what, List<string> fixes)
    {
        var hit = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        if (hit == null)
        {
            fixes.Add($"Unknown {what} \"{value}\"; using \"{fallback}\".");
        }

        return hit ?? fallback;
    }

    private static int Clamp(int value, int min, int max, string what, List<string> fixes)
    {
        var v = Math.Clamp(value, min, max);
        if (v != value)
        {
            fixes.Add($"{what} {value} is outside {min}–{max}; using {v}.");
        }

        return v;
    }

    private static double Clamp(double value, double min, double max, string what, List<string> fixes)
    {
        var v = double.IsNaN(value) ? min : Math.Clamp(value, min, max);
        if (Math.Abs(v - value) > 1e-9)
        {
            fixes.Add($"{what} {value} is outside {min}–{max}; using {v}.");
        }

        return v;
    }

    private static List<string> CleanList(List<string> values) =>
        (values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> Guids(List<string> values) =>
        (values ?? new()).Where(v => Guid.TryParse(v, out _)).Select(v => Guid.Parse(v).ToString("N")).Distinct().ToList();

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
