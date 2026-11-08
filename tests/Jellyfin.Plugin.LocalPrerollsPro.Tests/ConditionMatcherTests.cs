using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class ConditionMatcherTests
{
    private static FeatureContext At(DateTime now) => new() { Now = now };

    private static RuleCondition Range(string kind, string from, string to) => new() { Kind = kind, From = from, To = to };

    [Theory]
    [InlineData("2026-12-31", true)]
    [InlineData("2027-01-05", true)]
    [InlineData("2026-12-20", true)]
    [InlineData("2027-01-06", false)]
    [InlineData("2026-12-19", false)]
    [InlineData("2026-07-01", false)]
    public void YearlyRangeCanWrapTheYearEnd(string day, bool expected)
    {
        var c = Range(ConditionKinds.CurrentDate, "12-20", "01-05");
        Assert.Equal(expected, ConditionMatcher.Matches(c, At(DateTime.Parse(day).AddHours(20))));
    }

    [Fact]
    public void LastDayOfRangeMatchesAllDay()
    {
        // v1 stored the end as midnight and compared it with the current time, so the end day never matched.
        var c = Range(ConditionKinds.CurrentDate, "2026-12-24", "2026-12-25");
        Assert.True(ConditionMatcher.Matches(c, At(new DateTime(2026, 12, 25, 23, 30, 0))));
        Assert.False(ConditionMatcher.Matches(c, At(new DateTime(2026, 12, 26, 0, 1, 0))));
    }

    [Fact]
    public void LeapDayRuleDoesNotThrowInOtherYears()
    {
        // v1 built new DateTime(year, 2, 29), which throws outside leap years.
        var c = Range(ConditionKinds.CurrentDate, "02-29", "03-01");
        Assert.True(ConditionMatcher.Matches(c, At(new DateTime(2027, 3, 1))));
        Assert.False(ConditionMatcher.Matches(c, At(new DateTime(2027, 2, 28))));
        Assert.True(ConditionMatcher.Matches(c, At(new DateTime(2028, 2, 29))));
    }

    [Fact]
    public void OldIsoDateTimesStillParse()
    {
        var c = Range(ConditionKinds.CurrentDate, "2026-10-01T00:00:00", "2026-10-31T00:00:00");
        Assert.True(ConditionMatcher.Matches(c, At(new DateTime(2026, 10, 31, 22, 0, 0))));
    }

    [Fact]
    public void OpenEndedRanges()
    {
        Assert.True(ConditionMatcher.Matches(Range(ConditionKinds.ProductionYear, "1980", ""), new FeatureContext { ProductionYear = 2001 }));
        Assert.False(ConditionMatcher.Matches(Range(ConditionKinds.ProductionYear, "", "1989"), new FeatureContext { ProductionYear = 2001 }));
        Assert.True(ConditionMatcher.Matches(Range(ConditionKinds.ProductionYear, "1980", "1989"), new FeatureContext { ProductionYear = 1985 }));
        Assert.False(ConditionMatcher.Matches(Range(ConditionKinds.ProductionYear, "", ""), new FeatureContext { ProductionYear = 1985 }));
    }

    [Fact]
    public void PremiereDateUsesTheFeatureDate()
    {
        var c = Range(ConditionKinds.PremiereDate, "1980-01-01", "1989-12-31");
        Assert.True(ConditionMatcher.Matches(c, new FeatureContext { PremiereDate = new DateTime(1985, 7, 3) }));
        Assert.False(ConditionMatcher.Matches(c, new FeatureContext { PremiereDate = null }));
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(1, 59, true)]
    [InlineData(2, 0, false)]
    [InlineData(12, 0, false)]
    public void TimeOfDayCanWrapMidnight(int h, int m, bool expected)
    {
        var c = Range(ConditionKinds.TimeOfDay, "22:00", "02:00");
        Assert.Equal(expected, ConditionMatcher.Matches(c, At(new DateTime(2026, 5, 5, h, m, 0))));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(2, true)]
    [InlineData(15, false)]
    public void DayOfMonthCanWrap(int day, bool expected)
    {
        var c = Range(ConditionKinds.DayOfMonth, "28", "3");
        Assert.Equal(expected, ConditionMatcher.Matches(c, At(new DateTime(2026, 1, day))));
    }

    [Fact]
    public void DayOfWeekList()
    {
        var c = new RuleCondition { Kind = ConditionKinds.DayOfWeek, Values = new() { "Friday", "saturday" } };
        Assert.True(ConditionMatcher.Matches(c, At(new DateTime(2026, 11, 7)))); // Saturday
        Assert.False(ConditionMatcher.Matches(c, At(new DateTime(2026, 11, 8)))); // Sunday
    }

    [Fact]
    public void ListMatchingIsCaseInsensitiveAndCanBeNegated()
    {
        var ctx = new FeatureContext { Genres = FeatureContext.Set(new[] { "Horror", "Comedy" }) };
        var c = new RuleCondition { Kind = ConditionKinds.Genre, Values = new() { "horror" } };
        Assert.True(ConditionMatcher.Matches(c, ctx));
        c.Negate = true;
        Assert.False(ConditionMatcher.Matches(c, ctx));
    }

    [Fact]
    public void IdsMatchInAnyGuidFormat()
    {
        var id = Guid.NewGuid();
        var ctx = new FeatureContext { LibraryIds = FeatureContext.Set(new[] { id.ToString("N") }), UserId = id.ToString("N") };
        Assert.True(ConditionMatcher.Matches(new RuleCondition { Kind = ConditionKinds.Library, Values = new() { id.ToString("D").ToUpperInvariant() } }, ctx));
        Assert.True(ConditionMatcher.Matches(new RuleCondition { Kind = ConditionKinds.User, Values = new() { id.ToString("D") } }, ctx));
    }

    [Fact]
    public void UnknownKindNeverMatches()
    {
        Assert.False(ConditionMatcher.Matches(new RuleCondition { Kind = "Nonsense", Values = new() { "x" } }, new FeatureContext()));
    }

    [Fact]
    public void RuleMatchModes()
    {
        var ctx = new FeatureContext { Genres = FeatureContext.Set(new[] { "Horror" }), Resolution = "4K" };
        var rule = new IntroRule
        {
            Conditions = new List<RuleCondition>
            {
                new() { Kind = ConditionKinds.Genre, Values = new() { "Horror" } },
                new() { Kind = ConditionKinds.Resolution, Values = new() { "1080p" } },
            },
        };
        Assert.False(ConditionMatcher.RuleMatches(rule, ctx));
        rule.MatchMode = "Any";
        Assert.True(ConditionMatcher.RuleMatches(rule, ctx));
        Assert.True(ConditionMatcher.RuleMatches(new IntroRule(), ctx)); // No conditions = always.
    }
}
