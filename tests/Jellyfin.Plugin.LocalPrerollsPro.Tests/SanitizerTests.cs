using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class SanitizerTests
{
    [Fact]
    public void OutOfRangeNumbersAreClamped()
    {
        var c = new PluginConfiguration { AvoidRepeatCount = -5, BoostNewFactor = 99999, TargetLoudness = 50, BingeGapMinutes = 0, MinMinutesBetweenIntros = -1, BoostNewDays = 9999 };
        var fixes = ConfigSanitizer.Sanitize(c);
        Assert.Equal((0, 10.0, -10.0, 1, 0, 365), (c.AvoidRepeatCount, c.BoostNewFactor, c.TargetLoudness, c.BingeGapMinutes, c.MinMinutesBetweenIntros, c.BoostNewDays));
        Assert.Equal(6, fixes.Count);
    }

    [Theory]
    [InlineData("../../etc", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData(".prerolls", true)]
    [InlineData("My Prerolls", true)]
    public void TitleFolderMustBeAPlainName(string name, bool ok)
    {
        Assert.Equal(ok, ConfigSanitizer.IsSafeFolderName(name));
        var c = new PluginConfiguration { TitleFolderName = name };
        ConfigSanitizer.Sanitize(c);
        Assert.Equal(ok ? name : ".prerolls", c.TitleFolderName);
    }

    [Fact]
    public void ExtensionsAreNormalized()
    {
        Assert.Equal(".mp4,.mkv", ConfigSanitizer.NormalizeExtensions("mp4 MKV ;; .. .mp4"));
        var c = new PluginConfiguration { VideoExtensions = ";; .." };
        ConfigSanitizer.Sanitize(c);
        Assert.Equal(PluginConfiguration.DefaultVideoExtensions, c.VideoExtensions);
    }

    [Fact]
    public void UnknownModesFallBackAndListsAreCleaned()
    {
        var c = new PluginConfiguration
        {
            SelectionMode = "Chaos",
            EpisodeMode = "x",
            EnabledItemTypes = new() { "movie", "Book", "Episode", "Movie" },
            IntroFolders = new() { " /a/ ", "", "/a", "/b" },
            ExcludedUserIds = new() { "not-a-guid", "6F9619FF-8B86-D011-B42D-00C04FC964FF" },
            Rules = new()
            {
                new IntroRule { Id = "x", Precedence = 99999, Weight = 0, MatchMode = "whatever", Intros = new() { " a.mp4 ", "A.MP4", "" },
                    Conditions = new() { new RuleCondition { Kind = "Bogus" }, new RuleCondition { Kind = "Genre", Values = new() { " Horror ", "" } } } },
                new IntroRule { Id = "x" },
            },
            PreShow = new() { new PreShowStep { Kind = "Nope", Count = 50, ApplyTo = new() { "Movie", "Book" } } },
            CatalogSubscriptions = new() { new CatalogSubscription { Kind = "Category", Value = "Holiday" }, new CatalogSubscription { Kind = "category", Value = "holiday" }, new CatalogSubscription { Value = " " } },
        };
        ConfigSanitizer.Sanitize(c);
        Assert.Equal(SelectionModes.WeightedRandom, c.SelectionMode);
        Assert.Equal(EpisodeModes.EveryEpisode, c.EpisodeMode);
        Assert.Equal(new[] { "Movie", "Episode" }, c.EnabledItemTypes);
        Assert.Equal(new[] { "/a", "/b" }, c.IntroFolders);
        Assert.Equal(new[] { "6f9619ff8b86d011b42d00c04fc964ff" }, c.ExcludedUserIds);
        var r = c.Rules[0];
        Assert.Equal((1000, 1, "All"), (r.Precedence, r.Weight, r.MatchMode));
        Assert.Equal(new[] { "a.mp4" }, r.Intros);
        Assert.Equal(new[] { "Horror" }, r.Conditions.Single().Values);
        Assert.NotEqual(c.Rules[0].Id, c.Rules[1].Id);
        Assert.Equal((PreShowStepKinds.Rules, 10), (c.PreShow[0].Kind, c.PreShow[0].Count));
        Assert.Equal(new[] { "Movie" }, c.PreShow[0].ApplyTo);
        Assert.Single(c.CatalogSubscriptions);
    }
}
