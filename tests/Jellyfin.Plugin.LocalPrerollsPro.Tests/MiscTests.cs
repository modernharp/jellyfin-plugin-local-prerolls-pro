using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LocalPrerollsPro.Tests;

public class MiscTests
{
    [Theory]
    [InlineData(3840, 1600, "4K")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 480, "SD")]
    [InlineData(null, null, "")]
    public void Resolution(int? w, int? h, string expected) => Assert.Equal(expected, MediaClassifier.Resolution(w, h));

    [Fact]
    public void VideoRanges()
    {
        Assert.Equal(new[] { "HDR", "DolbyVision", "HDR10" }, MediaClassifier.VideoRanges("DOVIWithHDR10"));
        Assert.Equal(new[] { "HDR", "HDR10+", "HDR10" }, MediaClassifier.VideoRanges("HDR10Plus"));
        Assert.Equal(new[] { "SDR" }, MediaClassifier.VideoRanges("SDR"));
        Assert.Empty(MediaClassifier.VideoRanges("Unknown"));
    }

    [Fact]
    public void AudioFormats()
    {
        Assert.Equal(new[] { "Atmos", "TrueHD", "7.1" }, MediaClassifier.AudioFormats("truehd", "Dolby TrueHD + Dolby Atmos", null, 8));
        Assert.Equal(new[] { "DTS-HD MA", "DTS", "5.1" }, MediaClassifier.AudioFormats("dts", "DTS-HD MA", null, 6));
        Assert.Equal(new[] { "AAC", "Stereo" }, MediaClassifier.AudioFormats("aac", "LC", null, 2));
    }

    [Fact]
    public void KeysAreRelativeToTheirFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "intros");
        Assert.Equal("xmas/a.mp4", IntroKeys.FromPath(Path.Combine(root, "xmas", "a.mp4"), new[] { root }));
        Assert.Equal("a.mp4", IntroKeys.FromPath(Path.Combine(root, "a.mp4"), new[] { root + Path.DirectorySeparatorChar }));
        Assert.Equal("b.mp4", IntroKeys.FromPath("/elsewhere/b.mp4", new[] { root }));
        Assert.Equal("xmas/", IntroKeys.FolderOf("xmas/a.mp4"));
        Assert.Equal("Neon City", IntroKeys.DisplayName("/x/jellyfin-pre-roll_Neon_City.mp4"));
        Assert.Contains(".mkv", IntroKeys.ParseExtensions("mp4, .mkv"));
    }

    [Fact]
    public void HistoryTracksRecentIntrosAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "intros-history-" + Guid.NewGuid().ToString("N") + ".json");
        var user = Guid.NewGuid().ToString("D");
        var now = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        try
        {
            using (var h = new PlaybackHistory(path, NullLogger<PlaybackHistory>.Instance))
            {
                h.RecordPicks(user, new[] { "a.mp4" }, "series1", null, now);
                h.RecordPicks(user, new[] { "b.mp4", "a.mp4" }, null, null, now.AddMinutes(5));
                h.RecordPlay("a.mp4", now);
                h.RecordSeriesActivity(user, "series1", now.AddMinutes(30));
                Assert.Equal(new[] { "a.mp4" }, h.RecentKeys(user, 1));
                Assert.Equal(2, h.RecentKeys(user, 5).Count);
            }

            using (var h = new PlaybackHistory(path, NullLogger<PlaybackHistory>.Instance))
            {
                Assert.Equal(1, h.PlayCount("A.MP4"));
                Assert.Equal(2, h.StatsSnapshot()["a.mp4"].Picks);
                Assert.Equal(now.AddMinutes(5), h.LastIntroUtc(user.Replace("-", string.Empty)));
                Assert.Equal(now.AddMinutes(30), h.SeriesActivityUtc(user, "series1"));
                Assert.Equal(now, h.SeriesIntroUtc(user, "series1"));
                h.ResetStats();
                Assert.Empty(h.StatsSnapshot());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
