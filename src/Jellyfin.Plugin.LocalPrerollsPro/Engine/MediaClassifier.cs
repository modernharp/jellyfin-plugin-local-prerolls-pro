using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalPrerollsPro.Engine;

/// <summary>Turns raw stream details into the labels rules match on (4K, HDR10, Atmos, 5.1, ...).</summary>
public static class MediaClassifier
{
    public static string Resolution(int? width, int? height)
    {
        int w = width ?? 0, h = height ?? 0;
        if (w <= 0 && h <= 0)
        {
            return string.Empty;
        }

        // Use either side so letterboxed (2.39:1) and portrait sources classify sensibly.
        if (w >= 3200 || h >= 2000)
        {
            return "4K";
        }

        if (w >= 1800 || h >= 1000)
        {
            return "1080p";
        }

        if (w >= 1200 || h >= 700)
        {
            return "720p";
        }

        return "SD";
    }

    /// <summary>Labels for a Jellyfin VideoRangeType name (e.g. "DOVIWithHDR10").</summary>
    public static IEnumerable<string> VideoRanges(string videoRangeType)
    {
        var t = videoRangeType ?? string.Empty;
        if (t.Length == 0 || t.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        if (t.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            yield return "SDR";
            yield break;
        }

        if (t.Equals("DOVIWithSDR", StringComparison.OrdinalIgnoreCase))
        {
            // Dolby Vision with an SDR base layer.
            yield return "DolbyVision";
            yield return "SDR";
            yield break;
        }

        yield return "HDR";
        if (t.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase))
        {
            yield return "DolbyVision";
        }

        if (t.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase))
        {
            yield return "HDR10+";
            yield return "HDR10";
        }
        else if (t.Contains("HDR10", StringComparison.OrdinalIgnoreCase))
        {
            yield return "HDR10";
        }

        if (t.Contains("HLG", StringComparison.OrdinalIgnoreCase))
        {
            yield return "HLG";
        }
    }

    /// <summary>Labels for one audio stream.</summary>
    public static IEnumerable<string> AudioFormats(string codec, string profile, string title, int? channels)
    {
        var c = (codec ?? string.Empty).ToLowerInvariant();
        var details = ((profile ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();

        if (details.Contains("atmos", StringComparison.Ordinal))
        {
            yield return "Atmos";
        }

        if (details.Contains("dts:x", StringComparison.Ordinal) || details.Contains("dts-x", StringComparison.Ordinal) || details.Contains("dts x", StringComparison.Ordinal))
        {
            yield return "DTS:X";
        }

        switch (c)
        {
            case "truehd":
            case "mlp":
                yield return "TrueHD";
                break;
            case "dts":
            case "dca":
                if (details.Contains("ma", StringComparison.Ordinal) && (details.Contains("hd", StringComparison.Ordinal) || details.Contains("master", StringComparison.Ordinal)))
                {
                    yield return "DTS-HD MA";
                }

                yield return "DTS";
                break;
            case "eac3":
                yield return "DD+";
                break;
            case "ac3":
                yield return "DD";
                break;
            case "aac":
                yield return "AAC";
                break;
            case "flac":
                yield return "FLAC";
                break;
            case "opus":
                yield return "Opus";
                break;
        }

        var ch = channels ?? 0;
        if (ch >= 8)
        {
            yield return "7.1";
        }
        else if (ch >= 6)
        {
            yield return "5.1";
        }
        else if (ch == 2)
        {
            yield return "Stereo";
        }
        else if (ch == 1)
        {
            yield return "Mono";
        }
    }
}
