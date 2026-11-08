using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class IntroSelectorTests
{
    private sealed class FixedRandom : IRandomSource
    {
        private readonly Queue<double> _values;

        public FixedRandom(params double[] values) => _values = new Queue<double>(values);

        public double NextDouble() => _values.Count > 0 ? _values.Dequeue() : 0;
    }

    private static readonly FeatureContext Horror = new() { ItemType = "Movie", Genres = FeatureContext.Set(new[] { "Horror" }) };

    private static IntroRule Rule(string intro, int precedence, int weight, string genre = null) => new()
    {
        Name = intro,
        Precedence = precedence,
        Weight = weight,
        Intros = new() { intro },
        Conditions = genre == null ? new() : new() { new RuleCondition { Kind = ConditionKinds.Genre, Values = new() { genre } } },
    };

    private static SelectionRequest Request(IEnumerable<IntroRule> rules, IRandomSource random, int count = 1, IEnumerable<string> keys = null) => new()
    {
        Rules = rules.ToList(),
        AvailableKeys = (keys ?? new[] { "a.mp4", "b.mp4", "c.mp4" }).ToList(),
        Count = count,
        Random = random,
    };

    [Fact]
    public void LowerTierWeightsNoLongerSkewTheTopTier()
    {
        // v1 summed weights over all tiers but only walked the top tier, so B won ~99% of the time.
        var rules = new[] { Rule("a.mp4", 2, 1, "Horror"), Rule("b.mp4", 2, 1, "Horror"), Rule("c.mp4", 1, 98, "Horror") };
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 100; i++)
        {
            var pick = IntroSelector.Select(Request(rules, new FixedRandom(i / 100.0)), Horror).Picks.Single();
            counts[pick] = counts.GetValueOrDefault(pick) + 1;
        }

        Assert.Equal(50, counts["a.mp4"]);
        Assert.Equal(50, counts["b.mp4"]);
        Assert.False(counts.ContainsKey("c.mp4"));
    }

    [Fact]
    public void WeightsAreRespected()
    {
        var rules = new[] { Rule("a.mp4", 1, 3), Rule("b.mp4", 1, 1) };
        Assert.Equal("a.mp4", IntroSelector.Select(Request(rules, new FixedRandom(0.74)), Horror).Picks.Single());
        Assert.Equal("b.mp4", IntroSelector.Select(Request(rules, new FixedRandom(0.76)), Horror).Picks.Single());
        var probs = IntroSelector.Select(Request(rules, new FixedRandom(0)), Horror).Candidates.ToDictionary(c => c.Key, c => c.Probability);
        Assert.Equal(0.75, probs["a.mp4"], 3);
        Assert.Equal(0.25, probs["b.mp4"], 3);
    }

    [Fact]
    public void NonMatchingRulesAreIgnored()
    {
        var rules = new[] { Rule("a.mp4", 5, 1, "Comedy"), Rule("b.mp4", 0, 1) };
        var result = IntroSelector.Select(Request(rules, new FixedRandom(0)), Horror);
        Assert.Equal("b.mp4", result.Picks.Single());
        Assert.False(result.Rules.Single(r => r.RuleName == "a.mp4").Matched);
    }

    [Fact]
    public void RuleWithMissingFileFallsToNextTier()
    {
        var rules = new[] { Rule("gone.mp4", 9, 1), Rule("b.mp4", 0, 1) };
        Assert.Equal("b.mp4", IntroSelector.Select(Request(rules, new FixedRandom(0)), Horror).Picks.Single());
    }

    [Fact]
    public void MultiplePicksAreDistinctAndFillFromLowerTiers()
    {
        var rules = new[] { Rule("a.mp4", 2, 1), Rule("b.mp4", 1, 1), Rule("c.mp4", 1, 1) };
        var picks = IntroSelector.Select(Request(rules, new FixedRandom(0, 0, 0), count: 3), Horror).Picks;
        Assert.Equal(new[] { "a.mp4", "b.mp4", "c.mp4" }, picks);
    }

    [Fact]
    public void RecentlyPlayedAreAvoidedWhileAlternativesExist()
    {
        var rules = new[] { new IntroRule { Precedence = 0, Intros = new() { "*" } } };
        var req = Request(rules, new FixedRandom(0));
        var withRecent = new SelectionRequest
        {
            Rules = req.Rules,
            AvailableKeys = req.AvailableKeys,
            Random = new FixedRandom(0),
            RecentKeys = new HashSet<string>(new[] { "a.mp4", "b.mp4" }, StringComparer.OrdinalIgnoreCase),
        };
        Assert.Equal("c.mp4", IntroSelector.Select(withRecent, Horror).Picks.Single());

        var allRecent = new SelectionRequest
        {
            Rules = req.Rules,
            AvailableKeys = req.AvailableKeys,
            Random = new FixedRandom(0),
            RecentKeys = new HashSet<string>(req.AvailableKeys, StringComparer.OrdinalIgnoreCase),
        };
        Assert.Single(IntroSelector.Select(allRecent, Horror).Picks);
    }

    [Fact]
    public void ExcludedKeysAreNeverPicked()
    {
        var rules = new[] { new IntroRule { Intros = new() { "*" } } };
        var req = new SelectionRequest
        {
            Rules = rules,
            AvailableKeys = new[] { "a.mp4", "b.mp4" },
            Count = 2,
            ExcludeKeys = new HashSet<string>(new[] { "a.mp4" }, StringComparer.OrdinalIgnoreCase),
            Random = new FixedRandom(0),
        };
        Assert.Equal(new[] { "b.mp4" }, IntroSelector.Select(req, Horror).Picks);
    }

    [Fact]
    public void FolderAndStarEntriesExpand()
    {
        var keys = new[] { "xmas/one.mp4", "xmas/two.mp4", "other.mp4", "xmasish.mp4" };
        Assert.Equal(new[] { "xmas/one.mp4", "xmas/two.mp4" }, IntroSelector.ResolveIntros(new[] { "xmas/" }, keys));
        Assert.Equal(4, IntroSelector.ResolveIntros(new[] { "*", "other.mp4" }, keys).Count());
        Assert.Equal(new[] { "other.mp4" }, IntroSelector.ResolveIntros(new[] { "OTHER.mp4", "missing.mp4" }, keys));
    }

    [Fact]
    public void SequentialRotatesThroughIntros()
    {
        var rules = new[] { new IntroRule { Intros = new() { "*" } } };
        string cursor = null;
        var seen = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var c = cursor;
            var result = IntroSelector.Select(
                new SelectionRequest { Rules = rules, AvailableKeys = new[] { "b.mp4", "a.mp4", "c.mp4" }, Mode = SelectionModes.Sequential, GetCursor = _ => c },
                Horror);
            seen.Add(result.Picks.Single());
            cursor = result.CursorUpdates.Single().Value;
        }

        Assert.Equal(new[] { "a.mp4", "b.mp4", "c.mp4", "a.mp4" }, seen);
    }

    [Fact]
    public void LeastPlayedPicksTheFreshestIntro()
    {
        var rules = new[] { new IntroRule { Intros = new() { "*" } } };
        var plays = new Dictionary<string, long> { ["a.mp4"] = 5, ["b.mp4"] = 1, ["c.mp4"] = 3 };
        var result = IntroSelector.Select(
            new SelectionRequest { Rules = rules, AvailableKeys = plays.Keys.ToList(), Mode = SelectionModes.LeastPlayed, PlayCount = k => plays[k], Random = new FixedRandom(0.9) },
            Horror);
        Assert.Equal("b.mp4", result.Picks.Single());
    }

    [Fact]
    public void DisabledRulesDoNotMatch()
    {
        var rule = Rule("a.mp4", 1, 1);
        rule.Enabled = false;
        Assert.Empty(IntroSelector.Select(Request(new[] { rule }, new FixedRandom(0)), Horror).Picks);
    }
}
