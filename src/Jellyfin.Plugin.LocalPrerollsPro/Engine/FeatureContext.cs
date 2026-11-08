using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

/// <summary>
/// Facts about the feature being played and the moment it is played.
/// Plain data, so rule matching can be tested without a Jellyfin server.
/// Ids are lower-case 32-character hex strings (Guid "N" format).
/// </summary>
public sealed class FeatureContext
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Jellyfin item kind, e.g. "Movie" or "Episode".</summary>
    public string ItemType { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string SeriesId { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public ISet<string> Tags { get; init; } = new HashSet<string>(Ci);

    public ISet<string> Genres { get; init; } = new HashSet<string>(Ci);

    public ISet<string> Studios { get; init; } = new HashSet<string>(Ci);

    public ISet<string> People { get; init; } = new HashSet<string>(Ci);

    public ISet<string> LibraryIds { get; init; } = new HashSet<string>(Ci);

    /// <summary>Collections (box sets) that contain the feature or its series.</summary>
    public ISet<string> CollectionIds { get; init; } = new HashSet<string>(Ci);

    public string OfficialRating { get; init; } = string.Empty;

    /// <summary>Comparable parental rating score of the title. Null = unrated.</summary>
    public int? RatingScore { get; init; }

    public int? ProductionYear { get; init; }

    public DateTime? PremiereDate { get; init; }

    /// <summary>"4K", "1080p", "720p", "SD", or empty when unknown.</summary>
    public string Resolution { get; init; } = string.Empty;

    /// <summary>e.g. "SDR", "HDR", "HDR10", "HDR10+", "HLG", "DolbyVision".</summary>
    public ISet<string> VideoRanges { get; init; } = new HashSet<string>(Ci);

    /// <summary>e.g. "Atmos", "TrueHD", "DTS:X", "DTS-HD MA", "DTS", "DD+", "DD", "AAC", "5.1", "7.1".</summary>
    public ISet<string> AudioFormats { get; init; } = new HashSet<string>(Ci);

    /// <summary>Server local time when the feature starts.</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    public static ISet<string> Set(IEnumerable<string> values)
    {
        var set = new HashSet<string>(Ci);
        if (values != null)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    set.Add(v.Trim());
                }
            }
        }

        return set;
    }
}
