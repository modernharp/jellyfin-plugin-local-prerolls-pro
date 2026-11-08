using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

/// <summary>
/// Settings for one intro, merged from its sidecar JSON file, the folder's prerolls.json
/// and the settings page (the settings page wins).
/// </summary>
public sealed class IntroMeta
{
    public string Name { get; set; }

    public ISet<string> Tags { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Multiplies the rule weight. 1 = normal.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Parental rating of the intro itself, e.g. "PG-13". Empty = suitable for everyone.</summary>
    public string Rating { get; set; } = string.Empty;

    /// <summary>Numeric form of <see cref="Rating"/>, filled in by the server. Null = unrated.</summary>
    public int? RatingScore { get; set; }

    /// <summary>Only play between these dates ("MM-dd" yearly or "yyyy-MM-dd").</summary>
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Who made it, shown in the settings page.</summary>
    public string Credit { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>When the plugin first saw this file. Used by the new-intro boost.</summary>
    public DateTime? FirstSeenUtc { get; set; }
}

public sealed class EligibilityOptions
{
    public bool AgeSafe { get; init; } = true;

    /// <summary>For unrated titles: true = any intro; false = only unrated intros.</summary>
    public bool UnratedTitlesAllowAll { get; init; }

    public int BoostNewDays { get; init; } = 7;

    public double BoostNewFactor { get; init; } = 2;
}

public sealed class EligibilityResult
{
    public List<string> Eligible { get; } = new();

    /// <summary>Key -> reason it was left out (shown in the test tool).</summary>
    public Dictionary<string, string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-intro checks that apply before any rule runs: on/off, schedule and age rating.</summary>
public static class IntroEligibility
{
    public static EligibilityResult Filter(IEnumerable<string> keys, Func<string, IntroMeta> meta, FeatureContext ctx, EligibilityOptions options)
    {
        var result = new EligibilityResult();
        foreach (var key in keys)
        {
            var m = meta(key);
            var reason = m == null ? null : Reason(m, ctx, options);
            if (reason == null)
            {
                result.Eligible.Add(key);
            }
            else
            {
                result.Excluded[key] = reason;
            }
        }

        return result;
    }

    private static string Reason(IntroMeta m, FeatureContext ctx, EligibilityOptions options)
    {
        if (!m.Enabled)
        {
            return "turned off";
        }

        if ((!string.IsNullOrWhiteSpace(m.From) || !string.IsNullOrWhiteSpace(m.To)) && !ConditionMatcher.DateInRange(ctx.Now, m.From, m.To))
        {
            return $"only plays {Describe(m.From)} to {Describe(m.To)}";
        }

        if (options.AgeSafe && m.RatingScore.HasValue)
        {
            if (ctx.RatingScore.HasValue)
            {
                if (m.RatingScore.Value > ctx.RatingScore.Value)
                {
                    return $"rated {m.Rating}, above the title's {ctx.OfficialRating}";
                }
            }
            else if (!options.UnratedTitlesAllowAll)
            {
                return $"rated {m.Rating} and the title has no rating";
            }
        }

        return null;
    }

    /// <summary>Weight multiplier from the intro's own weight and the new-intro boost.</summary>
    public static double WeightFactor(IntroMeta m, DateTime utcNow, EligibilityOptions options)
    {
        if (m == null)
        {
            return 1;
        }

        var w = m.Weight > 0 ? m.Weight : 1;
        if (options.BoostNewDays > 0 && options.BoostNewFactor > 1 && m.FirstSeenUtc.HasValue
            && utcNow - m.FirstSeenUtc.Value < TimeSpan.FromDays(options.BoostNewDays))
        {
            w *= options.BoostNewFactor;
        }

        return w;
    }

    /// <summary>Combines a rating score and sub-score into one comparable number.</summary>
    public static int Combine(int score, int? subScore) => (score * 1000) + Math.Clamp(subScore ?? 0, 0, 999);

    private static string Describe(string bound) => string.IsNullOrWhiteSpace(bound) ? "any time" : bound;
}

/// <summary>Merges intro settings from several sources.</summary>
public static class IntroMetaMerge
{
    /// <summary>Later sources win for single values; tags are combined.</summary>
    public static IntroMeta Merge(params IntroMetaPatch[] patches)
    {
        var m = new IntroMeta();
        foreach (var p in patches.Where(p => p != null))
        {
            if (!string.IsNullOrWhiteSpace(p.Name))
            {
                m.Name = p.Name.Trim();
            }

            foreach (var t in p.Tags ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(t))
                {
                    m.Tags.Add(t.Trim());
                }
            }

            if (p.Weight is > 0)
            {
                m.Weight = p.Weight.Value;
            }

            if (p.Rating != null)
            {
                m.Rating = p.Rating.Trim();
            }

            if (p.From != null)
            {
                m.From = p.From.Trim();
            }

            if (p.To != null)
            {
                m.To = p.To.Trim();
            }

            if (p.Enabled.HasValue)
            {
                m.Enabled = p.Enabled.Value;
            }

            if (!string.IsNullOrWhiteSpace(p.Credit))
            {
                m.Credit = p.Credit.Trim();
            }

            if (!string.IsNullOrWhiteSpace(p.Url))
            {
                m.SourceUrl = p.Url.Trim();
            }
        }

        return m;
    }
}

/// <summary>
/// Partial intro settings as written in a sidecar JSON file or saved from the settings page.
/// Null means "not set here".
/// </summary>
public class IntroMetaPatch
{
    public string Name { get; set; }

    public List<string> Tags { get; set; }

    public double? Weight { get; set; }

    public string Rating { get; set; }

    public string From { get; set; }

    public string To { get; set; }

    public bool? Enabled { get; set; }

    public string Credit { get; set; }

    public string Url { get; set; }
}
