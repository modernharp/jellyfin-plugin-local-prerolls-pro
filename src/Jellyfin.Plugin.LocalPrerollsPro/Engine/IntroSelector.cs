using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

public interface IRandomSource
{
    /// <summary>Returns a number in [0, 1).</summary>
    double NextDouble();
}

public sealed class SharedRandomSource : IRandomSource
{
    public static readonly SharedRandomSource Instance = new();

    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class SelectionRequest
{
    public IReadOnlyList<IntroRule> Rules { get; init; } = Array.Empty<IntroRule>();

    /// <summary>Keys of the intro files that exist right now.</summary>
    public IReadOnlyCollection<string> AvailableKeys { get; init; } = Array.Empty<string>();

    public int Count { get; init; } = 1;

    public string Mode { get; init; } = SelectionModes.WeightedRandom;

    /// <summary>Intros the user saw recently. Avoided while other candidates remain.</summary>
    public ISet<string> RecentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Intros already chosen by an earlier pre-show step. Never picked again.</summary>
    public ISet<string> ExcludeKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Func<string, long> PlayCount { get; init; } = _ => 0;

    /// <summary>Sequential mode: last key played for a candidate pool (by pool signature).</summary>
    public Func<string, string> GetCursor { get; init; } = _ => null;

    public IRandomSource Random { get; init; } = SharedRandomSource.Instance;

    /// <summary>Tags of an intro, for "tag:name" entries in rules.</summary>
    public Func<string, ISet<string>> TagsOf { get; init; } = _ => null;

    /// <summary>Per-intro weight multiplier (its own weight and the new-intro boost).</summary>
    public Func<string, double> IntroWeight { get; init; } = _ => 1;
}

public sealed class RuleEvaluation
{
    public string RuleId { get; init; }

    public string RuleName { get; init; }

    public int Precedence { get; init; }

    public bool Matched { get; init; }

    public int AvailableIntros { get; init; }

    /// <summary>True when this rule's tier supplied candidates.</summary>
    public bool Used { get; set; }
}

public sealed class Candidate
{
    public string Key { get; init; }

    public int Precedence { get; init; }

    public double Weight { get; set; }

    /// <summary>Chance of being the first pick (0-1), shown in the test tool.</summary>
    public double Probability { get; set; }

    public bool RecentlyPlayed { get; init; }
}

public sealed class SelectionResult
{
    public List<string> Picks { get; } = new();

    public List<RuleEvaluation> Rules { get; } = new();

    public List<Candidate> Candidates { get; } = new();

    /// <summary>Sequential mode: pool signature and the key to store as the new cursor.</summary>
    public List<KeyValuePair<string, string>> CursorUpdates { get; } = new();
}

/// <summary>
/// Chooses intros from the matching rules.
/// Only the highest-precedence tier that has available intros is used; if more intros are
/// needed than that tier holds, the next tier fills the remaining slots.
/// </summary>
public static class IntroSelector
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static SelectionResult Select(SelectionRequest request, FeatureContext ctx)
    {
        var result = new SelectionResult();
        var available = request.AvailableKeys.ToList();

        // Evaluate every rule so the test tool can show why each one did or did not apply.
        var matched = new List<(IntroRule Rule, List<string> Keys, RuleEvaluation Eval)>();
        foreach (var rule in request.Rules)
        {
            var isMatch = rule.Enabled && ConditionMatcher.RuleMatches(rule, ctx);
            var keys = isMatch ? ResolveIntros(rule.Intros, available, request.TagsOf).ToList() : new List<string>();
            var eval = new RuleEvaluation
            {
                RuleId = rule.Id,
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed rule)" : rule.Name,
                Precedence = rule.Precedence,
                Matched = isMatch,
                AvailableIntros = keys.Count,
            };
            result.Rules.Add(eval);
            if (isMatch && keys.Count > 0)
            {
                matched.Add((rule, keys, eval));
            }
        }

        var tiers = matched.GroupBy(m => m.Rule.Precedence).OrderByDescending(g => g.Key).ToList();
        var wanted = Math.Max(0, request.Count);
        var picked = new HashSet<string>(request.ExcludeKeys ?? new HashSet<string>(), Ci);

        foreach (var tier in tiers)
        {
            if (result.Picks.Count >= wanted)
            {
                break;
            }

            // Weight per intro. An intro listed by several rules in the tier adds their weights.
            var weights = new Dictionary<string, double>(Ci);
            foreach (var (rule, keys, _) in tier)
            {
                var ruleWeight = Math.Max(1, rule.Weight);
                foreach (var k in keys.Distinct(Ci))
                {
                    var w = ruleWeight * Math.Max(0.01, request.IntroWeight(k));
                    weights[k] = weights.TryGetValue(k, out var cur) ? cur + w : w;
                }
            }

            foreach (var k in picked)
            {
                weights.Remove(k);
            }

            if (weights.Count == 0)
            {
                continue;
            }

            foreach (var (_, _, eval) in tier)
            {
                eval.Used = true;
            }

            var tierCandidates = weights
                .OrderBy(kv => kv.Key, Ci)
                .Select(kv => new Candidate
                {
                    Key = kv.Key,
                    Precedence = tier.Key,
                    Weight = kv.Value,
                    RecentlyPlayed = request.RecentKeys.Contains(kv.Key),
                })
                .ToList();
            FillProbabilities(tierCandidates, request.Mode, request.PlayCount);
            result.Candidates.AddRange(tierCandidates);

            var signature = "p" + tier.Key + ":" + string.Join("|", tierCandidates.Select(c => c.Key));
            var cursor = request.GetCursor(signature);

            while (result.Picks.Count < wanted && weights.Count > 0)
            {
                var pool = weights.Where(kv => !request.RecentKeys.Contains(kv.Key)).ToList();
                if (pool.Count == 0)
                {
                    pool = weights.ToList();
                }

                var choice = request.Mode switch
                {
                    SelectionModes.Sequential => NextInOrder(pool, cursor),
                    SelectionModes.LeastPlayed => LeastPlayed(pool, request.PlayCount, request.Random),
                    _ => Weighted(pool, request.Random),
                };

                result.Picks.Add(choice);
                picked.Add(choice);
                weights.Remove(choice);
                cursor = choice;
            }

            if (request.Mode == SelectionModes.Sequential && cursor != null)
            {
                result.CursorUpdates.Add(new KeyValuePair<string, string>(signature, cursor));
            }
        }

        return result;
    }

    /// <summary>Expands "*", "folder/", "tag:name" and exact keys to the matching available keys.</summary>
    public static IEnumerable<string> ResolveIntros(IEnumerable<string> entries, IReadOnlyCollection<string> available, Func<string, ISet<string>> tagsOf = null)
    {
        var seen = new HashSet<string>(Ci);
        foreach (var raw in entries ?? Enumerable.Empty<string>())
        {
            var entry = (raw ?? string.Empty).Trim().Replace('\\', '/');
            if (entry.Length == 0)
            {
                continue;
            }

            IEnumerable<string> matches;
            if (entry == "*")
            {
                matches = available;
            }
            else if (entry.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = entry[4..].Trim();
                matches = tagsOf == null || tag.Length == 0
                    ? Enumerable.Empty<string>()
                    : available.Where(k => tagsOf(k)?.Contains(tag) == true);
            }
            else if (entry.EndsWith('/'))
            {
                matches = available.Where(k => k.StartsWith(entry, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                matches = available.Where(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var m in matches)
            {
                if (seen.Add(m))
                {
                    yield return m;
                }
            }
        }
    }

    private static string Weighted(List<KeyValuePair<string, double>> pool, IRandomSource random)
    {
        var total = pool.Sum(kv => kv.Value);
        var roll = random.NextDouble() * total;
        foreach (var kv in pool.OrderBy(kv => kv.Key, Ci))
        {
            if (roll < kv.Value)
            {
                return kv.Key;
            }

            roll -= kv.Value;
        }

        return pool.OrderBy(kv => kv.Key, Ci).Last().Key; // Floating point edge case only.
    }

    private static string LeastPlayed(List<KeyValuePair<string, double>> pool, Func<string, long> playCount, IRandomSource random)
    {
        var min = pool.Min(kv => playCount(kv.Key));
        return Weighted(pool.Where(kv => playCount(kv.Key) == min).ToList(), random);
    }

    private static string NextInOrder(List<KeyValuePair<string, double>> pool, string cursor)
    {
        var ordered = pool.Select(kv => kv.Key).OrderBy(k => k, Ci).ToList();
        if (cursor != null)
        {
            var next = ordered.FirstOrDefault(k => Ci.Compare(k, cursor) > 0);
            if (next != null)
            {
                return next;
            }
        }

        return ordered[0];
    }

    private static void FillProbabilities(List<Candidate> candidates, string mode, Func<string, long> playCount)
    {
        // Ignores the recently-played filter, so this is the "fresh" chance of each intro.
        if (mode == SelectionModes.Sequential)
        {
            foreach (var c in candidates)
            {
                c.Probability = 1.0 / candidates.Count;
            }

            return;
        }

        var pool = candidates;
        if (mode == SelectionModes.LeastPlayed)
        {
            var min = candidates.Min(c => playCount(c.Key));
            pool = candidates.Where(c => playCount(c.Key) == min).ToList();
        }

        var total = pool.Sum(c => c.Weight);
        foreach (var c in candidates)
        {
            c.Probability = pool.Contains(c) && total > 0 ? c.Weight / total : 0;
        }
    }
}
