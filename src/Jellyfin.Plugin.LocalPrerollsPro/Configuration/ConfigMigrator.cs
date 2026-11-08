using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;

namespace Jellyfin.Plugin.LocalPrerollsPro.Configuration;

/// <summary>
/// Upgrades settings from the original plugin (schema 0) and fills defaults on a fresh install.
/// v1 rules point at random library item ids; they are turned into stable file keys here.
/// </summary>
public static class ConfigMigrator
{
    /// <param name="config">Settings to upgrade in place.</param>
    /// <param name="introPathById">Looks up the file path of a v1 intro item id; null if unknown.</param>
    /// <returns>True when anything changed and the settings should be saved.</returns>
    public static bool Migrate(PluginConfiguration config, Func<Guid, string> introPathById)
    {
        if (config.SchemaVersion >= PluginConfiguration.CurrentSchemaVersion)
        {
            return false;
        }

        var hadLegacy = !string.IsNullOrWhiteSpace(config.Local)
            || config.DefaultLocalVideos.Count > 0
            || config.TagIntros.Count > 0
            || config.GenreIntros.Count > 0
            || config.StudioIntros.Count > 0
            || config.CurrentDateIntros.Count > 0
            || config.PremiereDateIntros.Count > 0;

        if (string.IsNullOrWhiteSpace(config.VideoExtensions))
        {
            config.VideoExtensions = PluginConfiguration.DefaultVideoExtensions;
        }

        if (config.IntroFolders.Count == 0 && !string.IsNullOrWhiteSpace(config.Local))
        {
            var local = config.Local.Trim();
            var root = LooksLikeFile(local, config.VideoExtensions) ? Path.GetDirectoryName(local) : local;
            if (!string.IsNullOrWhiteSpace(root))
            {
                config.IntroFolders.Add(root);
            }
        }

        string KeyFor(Guid id)
        {
            var path = introPathById(id);
            return string.IsNullOrWhiteSpace(path) ? null : IntroKeys.FromPath(path, config.IntroFolders);
        }

        var rules = new List<IntroRule>();

        void AddListRule(string kind, string value, Guid introId, int precedence, int prevalence)
        {
            var key = KeyFor(introId);
            if (key == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            rules.Add(new IntroRule
            {
                Name = $"{kind}: {value}",
                Precedence = Math.Max(1, precedence),
                Weight = Math.Max(1, prevalence),
                Intros = new List<string> { key },
                Conditions = new List<RuleCondition> { new() { Kind = kind, Values = new List<string> { value.Trim() } } },
            });
        }

        foreach (var r in config.TagIntros)
        {
            AddListRule(ConditionKinds.Tag, r.TagName, r.IntroId, r.Precedence, r.Prevalence);
        }

        foreach (var r in config.GenreIntros)
        {
            AddListRule(ConditionKinds.Genre, r.GenreName, r.IntroId, r.Precedence, r.Prevalence);
        }

        foreach (var r in config.StudioIntros)
        {
            AddListRule(ConditionKinds.Studio, r.StudioName, r.IntroId, r.Precedence, r.Prevalence);
        }

        foreach (var r in config.CurrentDateIntros)
        {
            AddDateRule(rules, r, ConditionKinds.CurrentDate, "Date", KeyFor(r.IntroId));
        }

        // v1 stored these but never used them. Migrated so they now work as intended.
        foreach (var r in config.PremiereDateIntros)
        {
            AddDateRule(rules, r, ConditionKinds.PremiereDate, "Premiere", KeyFor(r.IntroId));
        }

        var defaults = config.DefaultLocalVideos.Distinct().Select(KeyFor).Where(k => k != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (defaults.Count > 0)
        {
            rules.Add(new IntroRule { Name = "Default intros", Precedence = 0, Intros = defaults });
        }
        else if (!hadLegacy)
        {
            // Fresh install: every intro in the folders plays, including ones added later.
            rules.Add(new IntroRule { Name = "All pre-rolls", Precedence = 0, Intros = new List<string> { "*" } });
        }

        config.Rules.AddRange(rules);

        if (config.EnabledItemTypes.Count == 0)
        {
            config.EnabledItemTypes = config.IntrosForMoviesOnly
                ? new List<string> { "Movie" }
                : hadLegacy
                    ? new List<string> { "Movie", "Episode", "MusicVideo", "Video" } // v1 played intros before any video.
                    : new List<string> { "Movie", "Episode" };
        }

        if (config.PreShow.Count == 0)
        {
            config.PreShow.Add(new PreShowStep { Kind = PreShowStepKinds.Rules, Count = 1 });
        }

        config.Local = string.Empty;
        config.DetectedLocalVideos = new List<IntroVideo>();
        config.DefaultLocalVideos = new List<Guid>();
        config.TagIntros = new List<TagIntro>();
        config.GenreIntros = new List<GenreIntro>();
        config.StudioIntros = new List<StudioIntro>();
        config.CurrentDateIntros = new List<DateRangeIntro>();
        config.PremiereDateIntros = new List<DateRangeIntro>();
        config.IntrosForMoviesOnly = false;
        config.SchemaVersion = PluginConfiguration.CurrentSchemaVersion;
        return true;
    }

    private static void AddDateRule(List<IntroRule> rules, DateRangeIntro r, string kind, string label, string key)
    {
        if (key == null)
        {
            return;
        }

        var condition = ConvertDateRange(r, kind);
        rules.Add(new IntroRule
        {
            Name = $"{label}: {Describe(condition)}",
            Precedence = Math.Max(1, r.Precedence),
            Weight = Math.Max(1, r.Prevalence),
            Intros = new List<string> { key },
            Conditions = new List<RuleCondition> { condition },
        });
    }

    /// <summary>Maps a v1 date range (with its repeat type) onto the equivalent v2 condition.</summary>
    public static RuleCondition ConvertDateRange(DateRangeIntro r, string kind)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (r.RepeatType)
        {
            case CurrentDateRepeatRangeType.Weekly when kind == ConditionKinds.CurrentDate:
                var days = new List<string>();
                var d = r.DateStart.DayOfWeek;
                for (var i = 0; i < 7; i++)
                {
                    days.Add(d.ToString());
                    if (d == r.DateEnd.DayOfWeek)
                    {
                        break;
                    }

                    d = (DayOfWeek)(((int)d + 1) % 7);
                }

                return new RuleCondition { Kind = ConditionKinds.DayOfWeek, Values = days };
            case CurrentDateRepeatRangeType.Monthly when kind == ConditionKinds.CurrentDate:
                return new RuleCondition
                {
                    Kind = ConditionKinds.DayOfMonth,
                    From = r.DateStart.Day.ToString(inv),
                    To = r.DateEnd.Day.ToString(inv),
                };
            case CurrentDateRepeatRangeType.Yearly:
                return new RuleCondition { Kind = kind, From = r.DateStart.ToString("MM-dd", inv), To = r.DateEnd.ToString("MM-dd", inv) };
            default:
                return new RuleCondition { Kind = kind, From = r.DateStart.ToString("yyyy-MM-dd", inv), To = r.DateEnd.ToString("yyyy-MM-dd", inv) };
        }
    }

    private static string Describe(RuleCondition c) =>
        c.Values.Count > 0 ? string.Join(", ", c.Values) : $"{c.From} to {c.To}";

    private static bool LooksLikeFile(string path, string extensions)
    {
        if (File.Exists(path))
        {
            return true;
        }

        if (Directory.Exists(path))
        {
            return false;
        }

        return IntroKeys.ParseExtensions(extensions).Contains(Path.GetExtension(path));
    }
}
