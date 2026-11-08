using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

/// <summary>Names for <see cref="RuleCondition.Kind"/>.</summary>
public static class ConditionKinds
{
    // List kinds: match when the feature has any of Values.
    public const string Tag = "Tag";
    public const string Genre = "Genre";
    public const string Studio = "Studio";
    public const string Person = "Person";
    public const string Library = "Library";
    public const string Collection = "Collection";
    public const string Series = "Series";
    public const string ItemType = "ItemType";
    public const string OfficialRating = "OfficialRating";
    public const string Resolution = "Resolution";
    public const string VideoRange = "VideoRange";
    public const string AudioFormat = "AudioFormat";
    public const string User = "User";
    public const string DayOfWeek = "DayOfWeek";

    // Range kinds: use From / To.
    /// <summary>Today's date. "MM-dd" repeats yearly (may wrap the year end); "yyyy-MM-dd" is one-off.</summary>
    public const string CurrentDate = "CurrentDate";

    /// <summary>Feature's premiere date, same formats as <see cref="CurrentDate"/>.</summary>
    public const string PremiereDate = "PremiereDate";

    /// <summary>Feature's production year, inclusive.</summary>
    public const string ProductionYear = "ProductionYear";

    /// <summary>"HH:mm" local time. From inclusive, To exclusive; may wrap midnight.</summary>
    public const string TimeOfDay = "TimeOfDay";

    /// <summary>Day of the month 1-31, inclusive; may wrap the month end.</summary>
    public const string DayOfMonth = "DayOfMonth";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Tag, Genre, Studio, Person, Library, Collection, Series, ItemType, OfficialRating,
        Resolution, VideoRange, AudioFormat, User, DayOfWeek,
        CurrentDate, PremiereDate, ProductionYear, TimeOfDay, DayOfMonth
    };
}

/// <summary>Evaluates rule conditions against a <see cref="FeatureContext"/>.</summary>
public static class ConditionMatcher
{
    public static bool RuleMatches(IntroRule rule, FeatureContext ctx)
    {
        var conditions = rule.Conditions ?? new List<RuleCondition>();
        if (conditions.Count == 0)
        {
            return true;
        }

        return string.Equals(rule.MatchMode, "Any", StringComparison.OrdinalIgnoreCase)
            ? conditions.Any(c => Matches(c, ctx))
            : conditions.All(c => Matches(c, ctx));
    }

    public static bool Matches(RuleCondition condition, FeatureContext ctx)
    {
        var result = MatchesRaw(condition, ctx);
        return condition.Negate ? !result : result;
    }

    private static bool MatchesRaw(RuleCondition c, FeatureContext ctx)
    {
        var values = (c.Values ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();

        switch (c.Kind)
        {
            case ConditionKinds.Tag: return AnyIn(values, ctx.Tags);
            case ConditionKinds.Genre: return AnyIn(values, ctx.Genres);
            case ConditionKinds.Studio: return AnyIn(values, ctx.Studios);
            case ConditionKinds.Person: return AnyIn(values, ctx.People);
            case ConditionKinds.Library: return AnyIn(values.Select(NormalizeId), ctx.LibraryIds);
            case ConditionKinds.Collection: return AnyIn(values.Select(NormalizeId), ctx.CollectionIds);
            case ConditionKinds.Series: return values.Select(NormalizeId).Contains(NormalizeId(ctx.SeriesId), StringComparer.OrdinalIgnoreCase);
            case ConditionKinds.User: return values.Select(NormalizeId).Contains(NormalizeId(ctx.UserId), StringComparer.OrdinalIgnoreCase);
            case ConditionKinds.ItemType: return values.Contains(ctx.ItemType, StringComparer.OrdinalIgnoreCase);
            case ConditionKinds.OfficialRating: return values.Contains(ctx.OfficialRating ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            case ConditionKinds.Resolution: return values.Contains(ctx.Resolution ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            case ConditionKinds.VideoRange: return AnyIn(values, ctx.VideoRanges);
            case ConditionKinds.AudioFormat: return AnyIn(values, ctx.AudioFormats);
            case ConditionKinds.DayOfWeek:
                return values.Any(v => Enum.TryParse<DayOfWeek>(v, true, out var d) && d == ctx.Now.DayOfWeek);
            case ConditionKinds.CurrentDate: return DateInRange(ctx.Now, c.From, c.To);
            case ConditionKinds.PremiereDate: return ctx.PremiereDate.HasValue && DateInRange(ctx.PremiereDate.Value, c.From, c.To);
            case ConditionKinds.ProductionYear: return ctx.ProductionYear.HasValue && IntInRange(ctx.ProductionYear.Value, c.From, c.To);
            case ConditionKinds.TimeOfDay: return TimeInRange(ctx.Now.TimeOfDay, c.From, c.To);
            case ConditionKinds.DayOfMonth: return DayOfMonthInRange(ctx.Now.Day, c.From, c.To);
            default: return false; // Unknown kinds never match, so a typo can't make a rule fire everywhere.
        }
    }

    private static bool AnyIn(IEnumerable<string> values, ISet<string> have) => values.Any(have.Contains);

    public static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        return Guid.TryParse(id.Trim(), out var g) ? g.ToString("N") : id.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Date range check, by calendar day, inclusive at both ends.
    /// "MM-dd" bounds repeat every year and may wrap the year end (12-20 to 01-05).
    /// "yyyy-MM-dd" bounds are absolute. An empty bound is open.
    /// </summary>
    public static bool DateInRange(DateTime value, string from, string to)
    {
        var hasFrom = TryParseDateBound(from, out var fromDate, out var fromYearly);
        var hasTo = TryParseDateBound(to, out var toDate, out var toYearly);
        if (!hasFrom && !hasTo)
        {
            return false;
        }

        var yearly = (!hasFrom || fromYearly) && (!hasTo || toYearly);
        if (yearly)
        {
            var v = (value.Month * 100) + value.Day;
            var f = hasFrom ? (fromDate.Month * 100) + fromDate.Day : 101;
            var t = hasTo ? (toDate.Month * 100) + toDate.Day : 1231;
            return f <= t ? v >= f && v <= t : v >= f || v <= t;
        }

        // Mixed or absolute bounds: compare full dates. A yearly bound mixed with an
        // absolute one takes the year of the value, which is the most useful reading.
        var day = value.Date;
        var start = hasFrom ? (fromYearly ? SafeDate(day.Year, fromDate.Month, fromDate.Day) : fromDate) : DateTime.MinValue;
        var end = hasTo ? (toYearly ? SafeDate(day.Year, toDate.Month, toDate.Day) : toDate) : DateTime.MaxValue.Date;
        return day >= start && day <= end;
    }

    /// <summary>Parses "yyyy-MM-dd" (also an ISO date-time) or "MM-dd". Feb 29 is allowed for yearly bounds.</summary>
    public static bool TryParseDateBound(string text, out DateTime date, out bool yearly)
    {
        date = default;
        yearly = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var parts = text.Split('-');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(2000, m))
        {
            yearly = true;
            date = new DateTime(2000, m, d); // 2000 is a leap year, so 02-29 is valid.
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var full))
        {
            date = full.Date;
            return true;
        }

        return false;
    }

    private static DateTime SafeDate(int year, int month, int day) =>
        new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    public static bool IntInRange(int value, string from, string to)
    {
        var hasFrom = int.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f);
        var hasTo = int.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t);
        if (!hasFrom && !hasTo)
        {
            return false;
        }

        return (!hasFrom || value >= f) && (!hasTo || value <= t);
    }

    /// <summary>"HH:mm" range, From inclusive, To exclusive. Wraps midnight when From is after To.</summary>
    public static bool TimeInRange(TimeSpan value, string from, string to)
    {
        var hasFrom = TimeSpan.TryParseExact(from?.Trim(), new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var f);
        var hasTo = TimeSpan.TryParseExact(to?.Trim(), new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var t);
        if (!hasFrom && !hasTo)
        {
            return false;
        }

        if (!hasFrom)
        {
            return value < t;
        }

        if (!hasTo)
        {
            return value >= f;
        }

        return f <= t ? value >= f && value < t : value >= f || value < t;
    }

    public static bool DayOfMonthInRange(int day, string from, string to)
    {
        var hasFrom = int.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f);
        var hasTo = int.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t);
        if (!hasFrom && !hasTo)
        {
            return false;
        }

        f = hasFrom ? f : 1;
        t = hasTo ? t : 31;
        return f <= t ? day >= f && day <= t : day >= f || day <= t;
    }
}
