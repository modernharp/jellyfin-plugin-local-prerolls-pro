using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class MigrationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "intros-test-root-does-not-exist");

    [Fact]
    public void FreshInstallGetsWorkingDefaults()
    {
        var config = new PluginConfiguration();
        Assert.True(ConfigMigrator.Migrate(config, _ => null));

        var rule = Assert.Single(config.Rules);
        Assert.Equal(new[] { "*" }, rule.Intros);
        Assert.Equal(0, rule.Precedence);
        Assert.Equal(new[] { "Movie", "Episode" }, config.EnabledItemTypes);
        Assert.Equal(PreShowStepKinds.Rules, Assert.Single(config.PreShow).Kind);
        Assert.Equal(PluginConfiguration.CurrentSchemaVersion, config.SchemaVersion);
        Assert.False(ConfigMigrator.Migrate(config, _ => null)); // Runs once.
    }

    [Fact]
    public void V1SettingsBecomeEquivalentRules()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), gone = Guid.NewGuid();
        var paths = new Dictionary<Guid, string>
        {
            [a] = Path.Combine(Root, "a.mp4"),
            [b] = Path.Combine(Root, "sub", "b.mp4"),
            [c] = Path.Combine(Root, "c.mkv"),
        };
        var config = new PluginConfiguration
        {
            Local = Root,
            IntrosForMoviesOnly = true,
            DefaultLocalVideos = new() { a, a, gone },
            TagIntros = new() { new TagIntro { TagName = "4K", IntroId = b, Precedence = 3, Prevalence = 5 } },
            GenreIntros = new() { new GenreIntro { GenreName = "Horror", IntroId = c, Precedence = 2, Prevalence = 1 } },
            StudioIntros = new() { new StudioIntro { StudioName = "A24", IntroId = gone, Precedence = 1, Prevalence = 1 } },
            CurrentDateIntros = new()
            {
                new DateRangeIntro { IntroId = c, DateStart = new DateTime(2026, 12, 20), DateEnd = new DateTime(2027, 1, 5), RepeatType = CurrentDateRepeatRangeType.Yearly, Precedence = 4, Prevalence = 1 },
                new DateRangeIntro { IntroId = a, DateStart = new DateTime(2026, 11, 6), DateEnd = new DateTime(2026, 11, 9), RepeatType = CurrentDateRepeatRangeType.Weekly, Precedence = 1, Prevalence = 1 },
            },
            PremiereDateIntros = new() { new DateRangeIntro { IntroId = a, DateStart = new DateTime(1980, 1, 1), DateEnd = new DateTime(1989, 12, 31), Precedence = 1, Prevalence = 2 } },
        };

        Assert.True(ConfigMigrator.Migrate(config, id => paths.GetValueOrDefault(id)));

        Assert.Equal(new[] { Root }, config.IntroFolders);
        Assert.Equal(new[] { "Movie" }, config.EnabledItemTypes);
        Assert.Equal(string.Empty, config.Local);
        Assert.Empty(config.TagIntros);

        var tag = config.Rules.Single(r => r.Conditions.Any(x => x.Kind == ConditionKinds.Tag));
        Assert.Equal(new[] { "sub/b.mp4" }, tag.Intros);
        Assert.Equal((3, 5), (tag.Precedence, tag.Weight));

        Assert.DoesNotContain(config.Rules, r => r.Name.Contains("A24", StringComparison.Ordinal)); // Its intro no longer exists.

        var yearly = config.Rules.Single(r => r.Conditions.Any(x => x.Kind == ConditionKinds.CurrentDate)).Conditions.Single();
        Assert.Equal(("12-20", "01-05"), (yearly.From, yearly.To));

        var weekly = config.Rules.Single(r => r.Conditions.Any(x => x.Kind == ConditionKinds.DayOfWeek)).Conditions.Single();
        Assert.Equal(new[] { "Friday", "Saturday", "Sunday", "Monday" }, weekly.Values);

        var premiere = config.Rules.Single(r => r.Conditions.Any(x => x.Kind == ConditionKinds.PremiereDate)).Conditions.Single();
        Assert.Equal(("1980-01-01", "1989-12-31"), (premiere.From, premiere.To));

        var defaults = config.Rules.Single(r => r.Precedence == 0);
        Assert.Equal(new[] { "a.mp4" }, defaults.Intros);
        Assert.DoesNotContain(config.Rules, r => r.Intros.Contains("*"));
    }

    [Fact]
    public void MonthlyBecomesDayOfMonth()
    {
        var c = ConfigMigrator.ConvertDateRange(
            new DateRangeIntro { DateStart = new DateTime(2026, 1, 28), DateEnd = new DateTime(2026, 2, 3), RepeatType = CurrentDateRepeatRangeType.Monthly },
            ConditionKinds.CurrentDate);
        Assert.Equal((ConditionKinds.DayOfMonth, "28", "3"), (c.Kind, c.From, c.To));
    }

    [Fact]
    public void SingleFileSourceUsesItsFolder()
    {
        var file = Path.Combine(Root, "only.mp4");
        var id = Guid.NewGuid();
        var config = new PluginConfiguration { Local = file, DefaultLocalVideos = new() { id } };
        ConfigMigrator.Migrate(config, _ => file);
        Assert.Equal(new[] { Root }, config.IntroFolders);
        Assert.Equal(new[] { "only.mp4" }, config.Rules.Single().Intros);
        Assert.Equal(4, config.EnabledItemTypes.Count); // v1 played intros before any video.
    }
}
