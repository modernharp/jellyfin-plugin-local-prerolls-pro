using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

/// <summary>Collects the facts about a feature that rules can match on.</summary>
public sealed class FeatureContextBuilder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IntroCatalog _catalog;
    private readonly ILogger<FeatureContextBuilder> _logger;

    public FeatureContextBuilder(ILibraryManager libraryManager, IntroCatalog catalog, ILogger<FeatureContextBuilder> logger)
    {
        _libraryManager = libraryManager;
        _catalog = catalog;
        _logger = logger;
    }

    public FeatureContext Build(BaseItem item, User user, DateTime now, IReadOnlyList<IntroRule> rules)
    {
        var conditions = rules.Where(r => r.Enabled).SelectMany(r => r.Conditions ?? new List<RuleCondition>()).ToList();
        var episode = item as Episode;
        Season season = null;
        Series series = null;
        if (episode != null)
        {
            // Either can be missing, e.g. for episodes stored without season folders.
            season = Safe(() => episode.Season);
            series = Safe(() => episode.Series);
        }

        var related = new BaseItem[] { item, season, series }.Where(x => x != null).ToList();

        var rating = !string.IsNullOrWhiteSpace(item.OfficialRating) ? item.OfficialRating : series?.OfficialRating ?? string.Empty;
        var streams = Safe(() => item.GetMediaStreams()) ?? new List<MediaStream>();
        var video = streams.Where(s => s.Type == MediaStreamType.Video).OrderByDescending(s => s.IsDefault).FirstOrDefault();
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio)
            .SelectMany(s => MediaClassifier.AudioFormats(s.Codec, s.Profile, s.Title, s.Channels));

        return new FeatureContext
        {
            ItemType = item.GetBaseItemKind().ToString(),
            ItemId = item.Id.ToString("N"),
            SeriesId = series?.Id.ToString("N") ?? string.Empty,
            UserId = user?.Id.ToString("N") ?? string.Empty,
            Tags = FeatureContext.Set(related.SelectMany(x => x.Tags ?? Array.Empty<string>())),
            Genres = FeatureContext.Set(related.SelectMany(x => x.Genres ?? Array.Empty<string>())),
            Studios = FeatureContext.Set(related.SelectMany(x => x.Studios ?? Array.Empty<string>())),
            People = FeatureContext.Set(conditions.Any(c => c.Kind == ConditionKinds.Person) ? People(related) : null),
            LibraryIds = FeatureContext.Set(Safe(() => _libraryManager.GetCollectionFolders(item))?.Select(f => f.Id.ToString("N"))),
            CollectionIds = FeatureContext.Set(Collections(conditions, item, series)),
            OfficialRating = rating,
            RatingScore = _catalog.RatingScore(rating),
            ProductionYear = item.ProductionYear ?? series?.ProductionYear,
            PremiereDate = item.PremiereDate ?? season?.PremiereDate ?? series?.PremiereDate,
            Resolution = MediaClassifier.Resolution(video?.Width ?? item.Width, video?.Height ?? item.Height),
            VideoRanges = FeatureContext.Set(video != null ? MediaClassifier.VideoRanges(video.VideoRangeType.ToString()) : null),
            AudioFormats = FeatureContext.Set(audio),
            Now = now,
        };
    }

    private IEnumerable<string> People(IEnumerable<BaseItem> items)
    {
        foreach (var x in items)
        {
            var names = Safe(() => _libraryManager.GetPeopleNames(new InternalPeopleQuery { ItemId = x.Id }));
            foreach (var n in names ?? new List<string>())
            {
                yield return n;
            }
        }
    }

    /// <summary>Only checks collections that rules actually ask about, since there is no reverse lookup.</summary>
    private IEnumerable<string> Collections(IEnumerable<RuleCondition> conditions, BaseItem item, BaseItem series)
    {
        var ids = conditions.Where(c => c.Kind == ConditionKinds.Collection)
            .SelectMany(c => c.Values ?? new List<string>())
            .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct();

        foreach (var id in ids)
        {
            if (Safe(() => _libraryManager.GetItemById(id)) is not BoxSet boxSet)
            {
                continue;
            }

            var children = Safe(() => boxSet.GetLinkedChildren()) ?? new List<BaseItem>();
            if (children.Any(c => c.Id == item.Id || (series != null && c.Id == series.Id)))
            {
                yield return id.ToString("N");
            }
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
            _logger.LogDebug(ex, "Could not read feature details");
            return null;
        }
    }
}
