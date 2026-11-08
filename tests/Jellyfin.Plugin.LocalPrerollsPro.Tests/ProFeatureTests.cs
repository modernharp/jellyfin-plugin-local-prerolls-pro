using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class ProFeatureTests
{
    private static readonly EligibilityOptions Safe = new() { AgeSafe = true };

    private static FeatureContext Title(int? score, string rating = "PG", DateTime? now = null) =>
        new() { RatingScore = score, OfficialRating = rating, Now = now ?? new DateTime(2026, 11, 8, 20, 0, 0) };

    [Fact]
    public void AgeSafeBlocksIntrosRatedAboveTheTitle()
    {
        var meta = new Dictionary<string, IntroMeta>
        {
            ["r.mp4"] = new() { Rating = "R", RatingScore = 17000 },
            ["pg.mp4"] = new() { Rating = "PG", RatingScore = 10000 },
            ["any.mp4"] = new(),
        };
        var r = IntroEligibility.Filter(meta.Keys, k => meta[k], Title(10000), Safe);
        Assert.Equal(new[] { "pg.mp4", "any.mp4" }, r.Eligible);
        Assert.Contains("above the title", r.Excluded["r.mp4"]);

        var off = IntroEligibility.Filter(meta.Keys, k => meta[k], Title(10000), new EligibilityOptions { AgeSafe = false });
        Assert.Equal(3, off.Eligible.Count);
    }

    [Fact]
    public void UnratedTitlesOnlyGetUnratedIntrosUnlessAllowed()
    {
        var meta = new Dictionary<string, IntroMeta> { ["pg.mp4"] = new() { Rating = "PG", RatingScore = 10000 }, ["any.mp4"] = new() };
        Assert.Equal(new[] { "any.mp4" }, IntroEligibility.Filter(meta.Keys, k => meta[k], Title(null, string.Empty), Safe).Eligible);
        Assert.Equal(2, IntroEligibility.Filter(meta.Keys, k => meta[k], Title(null, string.Empty), new EligibilityOptions { AgeSafe = true, UnratedTitlesAllowAll = true }).Eligible.Count);
    }

    [Fact]
    public void ScheduledAndDisabledIntrosAreLeftOut()
    {
        var meta = new Dictionary<string, IntroMeta>
        {
            ["xmas.mp4"] = new() { From = "12-01", To = "12-26" },
            ["off.mp4"] = new() { Enabled = false },
            ["ok.mp4"] = new(),
        };
        var nov = IntroEligibility.Filter(meta.Keys, k => meta[k], Title(null, now: new DateTime(2026, 11, 8)), Safe);
        Assert.Equal(new[] { "ok.mp4" }, nov.Eligible);
        var dec = IntroEligibility.Filter(meta.Keys, k => meta[k], Title(null, now: new DateTime(2026, 12, 24)), Safe);
        Assert.Equal(new[] { "xmas.mp4", "ok.mp4" }, dec.Eligible);
    }

    [Fact]
    public void NewIntrosAreBoostedForTheirFirstDays()
    {
        var now = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var opts = new EligibilityOptions { BoostNewDays = 7, BoostNewFactor = 2 };
        Assert.Equal(6, IntroEligibility.WeightFactor(new IntroMeta { Weight = 3, FirstSeenUtc = now.AddDays(-2) }, now, opts));
        Assert.Equal(3, IntroEligibility.WeightFactor(new IntroMeta { Weight = 3, FirstSeenUtc = now.AddDays(-8) }, now, opts));
        Assert.Equal(1, IntroEligibility.WeightFactor(null, now, opts));
    }

    [Fact]
    public void LaterSettingsWinAndTagsCombine()
    {
        var m = IntroMetaMerge.Merge(
            new IntroMetaPatch { Name = "Folder default", Tags = new() { "logo" }, Weight = 2, Rating = "PG" },
            new IntroMetaPatch { Tags = new() { "christmas" }, Rating = "G", Enabled = false },
            null);
        Assert.Equal("Folder default", m.Name);
        Assert.Equal(2, m.Weight);
        Assert.Equal("G", m.Rating);
        Assert.False(m.Enabled);
        Assert.True(m.Tags.SetEquals(new[] { "logo", "christmas" }));
    }

    [Fact]
    public void RulesCanTargetIntroTagsAndIntroWeightsApply()
    {
        var tags = new Dictionary<string, ISet<string>>
        {
            ["a.mp4"] = new HashSet<string>(new[] { "Christmas" }, StringComparer.OrdinalIgnoreCase),
            ["b.mp4"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["c.mp4"] = new HashSet<string>(new[] { "christmas" }, StringComparer.OrdinalIgnoreCase),
        };
        Assert.Equal(new[] { "a.mp4", "c.mp4" }, IntroSelector.ResolveIntros(new[] { "tag:christmas" }, tags.Keys.ToList(), k => tags[k]));

        var result = IntroSelector.Select(
            new SelectionRequest
            {
                Rules = new[] { new IntroRule { Intros = new() { "tag:christmas" } } },
                AvailableKeys = tags.Keys.ToList(),
                TagsOf = k => tags[k],
                IntroWeight = k => k == "c.mp4" ? 3 : 1,
            },
            new FeatureContext());
        var p = result.Candidates.ToDictionary(c => c.Key, c => c.Probability);
        Assert.Equal(0.25, p["a.mp4"], 3);
        Assert.Equal(0.75, p["c.mp4"], 3);
    }

    [Fact]
    public void CatalogSubscriptionsMatchCategoryTagAndPlatform()
    {
        var item = new CatalogItem
        {
            Slug = "x",
            Platforms = new() { "jellyfin", "plex" },
            Category = new CatalogRef { Slug = "holiday", Name = "Holiday" },
            Tags = new() { new CatalogRef { Slug = "christmas", Name = "christmas" } },
        };
        Assert.True(CatalogService.Matches(item, new CatalogSubscription { Kind = "category", Value = "holiday" }));
        Assert.True(CatalogService.Matches(item, new CatalogSubscription { Kind = "tag", Value = "Christmas", Platform = "jellyfin" }));
        Assert.False(CatalogService.Matches(item, new CatalogSubscription { Kind = "tag", Value = "christmas", Platform = "emby" }));
        Assert.False(CatalogService.Matches(item, new CatalogSubscription { Kind = "category", Value = "studio-logos" }));
    }

    [Fact]
    public void DownloadNamesAreSafe()
    {
        Assert.Equal("x-logo-emby-pre-roll", CatalogService.SafeName("x-logo-emby-pre-roll"));
        Assert.Equal("a-b-c", CatalogService.SafeName("../a/b c"));
        Assert.Equal("preroll", CatalogService.SafeName("../.."));
    }

    [Fact]
    public void LoudnormSummaryIsParsed()
    {
        const string output = "[Parsed_loudnorm_0 @ 0x1]\n{\n\t\"input_i\" : \"-17.42\",\n\t\"input_tp\" : \"-0.31\",\n\t\"input_lra\" : \"3.10\"\n}";
        Assert.Equal(-17.42, IntroPreparer.ParseLoudnorm(output, "input_i"));
        Assert.Equal(-0.31, IntroPreparer.ParseLoudnorm(output, "input_tp"));
        Assert.Null(IntroPreparer.ParseLoudnorm("{ \"input_i\" : \"-inf\" }", "input_i"));
        Assert.Null(IntroPreparer.ParseLoudnorm(null, "input_i"));
    }
}
