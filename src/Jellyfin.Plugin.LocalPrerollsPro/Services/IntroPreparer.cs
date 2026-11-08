using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

public sealed class PrepRecord
{
    public string Key { get; set; }

    public string SourcePath { get; set; }

    public long SourceSize { get; set; }

    public long SourceModifiedTicks { get; set; }

    /// <summary>Waiting, Working, Ready (copy made), Original (no copy needed) or Failed.</summary>
    public string Status { get; set; } = "Waiting";

    public string Error { get; set; }

    /// <summary>Why a converted copy is needed, e.g. "video is hevc".</summary>
    public List<string> Problems { get; set; } = new();

    /// <summary>Integrated loudness in LUFS.</summary>
    public double? Loudness { get; set; }

    public double? TruePeak { get; set; }

    public string PreparedPath { get; set; }

    /// <summary>What the prepared copy fixes: "converted", "volume-matched" or both.</summary>
    public string PreparedFor { get; set; }

    /// <summary>Conversion settings the copy was made with. A change makes a new copy.</summary>
    public string Signature { get; set; }
}

/// <summary>
/// Background work on intro files: measures loudness, and makes H.264/AAC copies of files
/// that clients can't play directly, and volume-matched copies when levelling is on.
/// Copies live in Jellyfin's cache folder and are played instead of the originals.
/// </summary>
public sealed class IntroPreparer : IDisposable
{
    private static readonly HashSet<string> GoodAudio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3" };

    private readonly IntroCatalog _catalog;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<IntroPreparer> _logger;
    private readonly string _dir;
    private readonly string _stateFile;
    private readonly ConcurrentDictionary<string, PrepRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task _loop;

    public IntroPreparer(IntroCatalog catalog, IMediaEncoder mediaEncoder, IApplicationPaths paths, ILogger<IntroPreparer> logger)
    {
        _catalog = catalog;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _dir = Path.Combine(paths.CachePath, "local-prerolls-pro");
        _stateFile = Path.Combine(_dir, "prepared.json");
        Load();
        _catalog.Scanned += (_, _) => Trigger();
    }

    /// <summary>True while files are being analysed or converted.</summary>
    public bool Busy { get; private set; }

    /// <summary>Intro key whose prepared copy is at this path, or null.</summary>
    public string KeyForPreparedPath(string path) =>
        path == null ? null : _records.Values.FirstOrDefault(r => string.Equals(r.PreparedPath, path, StringComparison.OrdinalIgnoreCase))?.Key;

    public PrepRecord Record(string key) => key != null && _records.TryGetValue(key, out var r) ? r : null;

    public void Start()
    {
        _loop ??= Task.Run(LoopAsync);
    }

    public void Trigger()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>The file to stream for an intro: its prepared copy when ready and current, otherwise the original.</summary>
    public (string Path, Guid ItemId, bool Prepared) PlaybackTarget(IntroFile file)
    {
        var r = Record(file.Key);
        if (r != null && r.Status == "Ready" && r.PreparedPath != null && File.Exists(r.PreparedPath)
            && string.Equals(r.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase) && r.Signature == Signature() && SourceUnchanged(r))
        {
            try
            {
                return (r.PreparedPath, _catalog.EnsureItem(r.PreparedPath, _catalog.Meta(file.Key)?.Name ?? file.Name), true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not register prepared copy {Path}; using the original", r.PreparedPath);
            }
        }

        return (file.Path, file.ItemId, false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                await ProcessAllAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Intro preparation failed");
            }
            finally
            {
                Busy = false;
            }
        }
    }

    /// <summary>Analyses every intro that changed and makes any copies needed. Also usable from a scheduled task.</summary>
    public async Task ProcessAllAsync(CancellationToken ct)
    {
        Busy = true;
        var files = _catalog.Files.Values.ToList();
        var signature = Signature();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Path);
            if (!info.Exists)
            {
                continue;
            }

            var r = Record(file.Key);
            var analysed = r != null && string.Equals(r.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase)
                && r.SourceSize == info.Length && r.SourceModifiedTicks == info.LastWriteTimeUtc.Ticks
                && r.Status is "Ready" or "Original" or "Failed";
            if (analysed && r.Signature == signature)
            {
                continue;
            }

            if (!analysed)
            {
                r = new PrepRecord { Key = file.Key, SourcePath = file.Path, SourceSize = info.Length, SourceModifiedTicks = info.LastWriteTimeUtc.Ticks };
            }

            _records[file.Key] = r;
            r.Status = "Working";
            r.Error = null;
            try
            {
                await PrepareAsync(file, r, signature, needAnalysis: !analysed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                r.Status = "Waiting";
                throw;
            }
            catch (Exception ex)
            {
                r.Status = "Failed";
                r.Error = ex.Message;
                _logger.LogWarning("Could not prepare intro {Path}: {Message}", file.Path, ex.Message);
            }

            r.Signature = signature;
            Save();
        }

        // Remove copies of intros that are gone.
        var live = new HashSet<string>(files.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _records.Keys.Where(k => !live.Contains(k)).ToList())
        {
            if (_records.TryRemove(key, out var gone))
            {
                DeleteCopy(gone);
            }
        }

        CleanOrphans();
        Save();
    }

    private async Task PrepareAsync(IntroFile file, PrepRecord r, string signature, bool needAnalysis, CancellationToken ct)
    {
        var config = PrerollsPlugin.Instance.Configuration;
        var probe = needAnalysis || r.Problems == null ? await ProbeAsync(file.Path, ct).ConfigureAwait(false) : null;
        if (probe != null)
        {
            r.Problems = Problems(probe);
            if (probe.HasAudio)
            {
                var (i, tp) = await MeasureLoudnessAsync(file.Path, config.TargetLoudness, ct).ConfigureAwait(false);
                r.Loudness = i;
                r.TruePeak = tp;
            }
        }

        var convert = config.ConvertProblemFiles && r.Problems.Count > 0;
        var level = config.LevelLoudness && r.Loudness.HasValue && !double.IsInfinity(r.Loudness.Value)
            && Math.Abs(r.Loudness.Value - config.TargetLoudness) > 1.5;

        if (!convert && !level)
        {
            DeleteCopy(r);
            r.Status = "Original";
            r.PreparedFor = null;
            return;
        }

        probe ??= await ProbeAsync(file.Path, ct).ConfigureAwait(false);
        Directory.CreateDirectory(_dir);
        var output = Path.Combine(_dir, Hash(file.Path + "|" + r.SourceSize + "|" + r.SourceModifiedTicks + "|" + signature) + ".mp4");
        if (r.PreparedPath != null && r.PreparedPath != output)
        {
            DeleteCopy(r);
        }

        if (!File.Exists(output))
        {
            var args = BuildArguments(file.Path, output, probe, convert, level, r, config.TargetLoudness);
            _logger.LogInformation("Preparing intro {Path} ({What})", file.Path, Describe(convert, level));
            var tmp = output + ".part.mp4";
            args[^1] = tmp;
            var (code, err) = await RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(tmp))
            {
                TryDelete(tmp);
                throw new InvalidOperationException("ffmpeg failed: " + LastLines(err, 3));
            }

            File.Move(tmp, output, true);
        }

        r.PreparedPath = output;
        r.PreparedFor = Describe(convert, level);
        r.Status = "Ready";
        _catalog.EnsureItem(output, _catalog.Meta(file.Key)?.Name ?? file.Name);
    }

    private static string Describe(bool convert, bool level) =>
        convert && level ? "converted, volume-matched" : convert ? "converted" : "volume-matched";

    /// <summary>Builds the ffmpeg arguments. The last argument is the output path.</summary>
    private List<string> BuildArguments(string input, string output, ProbeResult probe, bool convert, bool level, PrepRecord r, double target)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        var silent = !probe.HasAudio;
        if (silent && convert)
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
        }

        args.AddRange(new[] { "-map", "0:v:0" });
        args.AddRange(silent && convert ? new[] { "-map", "1:a:0", "-shortest" } : new[] { "-map", "0:a:0?" });

        var videoOk = !convert || (string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) && !probe.IsHdr && !probe.Is10Bit);
        if (videoOk)
        {
            args.AddRange(new[] { "-c:v", "copy" });
        }
        else
        {
            var filters = new List<string>();
            if (probe.IsHdr && _mediaEncoder.SupportsFilter("zscale"))
            {
                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
            }

            filters.Add("scale='min(1920,iw)':-2");
            filters.Add("format=yuv420p");
            args.AddRange(new[] { "-vf", string.Join(",", filters), "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-profile:v", "high", "-level", "4.1" });
        }

        var audioOk = !silent && !level && (!convert || GoodAudio.Contains(probe.AudioCodec ?? string.Empty));
        if (audioOk || (silent && !convert))
        {
            args.AddRange(new[] { "-c:a", "copy" });
        }
        else
        {
            if (level && r.Loudness.HasValue)
            {
                var gain = target - r.Loudness.Value;
                var peakRoom = -1.0 - (r.TruePeak ?? -10); // Keep the true peak under -1 dBTP.
                gain = Math.Min(gain, peakRoom);
                args.AddRange(new[] { "-af", string.Create(CultureInfo.InvariantCulture, $"volume={gain:0.00}dB") });
            }

            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000" });
        }

        // Drop embedded titles so the copy shows the pre-roll's own name.
        args.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart", "-f", "mp4", output });
        return args;
    }

    private static List<string> Problems(ProbeResult p)
    {
        var list = new List<string>();
        if (p.VideoCodec == null)
        {
            list.Add("no video track");
            return list;
        }

        if (!string.Equals(p.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            list.Add($"video is {p.VideoCodec}");
        }
        else if (p.Is10Bit)
        {
            list.Add("video is 10-bit H.264");
        }

        if (p.IsHdr)
        {
            list.Add("video is HDR");
        }

        if (!p.HasAudio)
        {
            list.Add("no audio track");
        }
        else if (!GoodAudio.Contains(p.AudioCodec ?? string.Empty))
        {
            list.Add($"audio is {p.AudioCodec}");
        }

        var formats = (p.Format ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (formats.Length > 0 && !formats.Any(f => f is "mp4" or "mov" or "matroska" or "webm"))
        {
            list.Add($"container is {p.Format}");
        }

        return list;
    }

    private sealed class ProbeResult
    {
        public string VideoCodec { get; set; }

        public string AudioCodec { get; set; }

        public bool HasAudio { get; set; }

        public bool IsHdr { get; set; }

        public bool Is10Bit { get; set; }

        public string Format { get; set; }
    }

    private async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var probePath = string.IsNullOrEmpty(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath;
        var (code, output) = await RunAsync(probePath, new List<string> { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path }, TimeSpan.FromMinutes(2), ct, readStdout: true).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException("ffprobe could not read the file");
        }

        using var doc = JsonDocument.Parse(output);
        var result = new ProbeResult();
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("format_name", out var fn))
        {
            result.Format = fn.GetString();
        }

        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type == "video" && result.VideoCodec == null && !IsAttachedPic(s))
                {
                    result.VideoCodec = Str(s, "codec_name");
                    var pix = Str(s, "pix_fmt") ?? string.Empty;
                    result.Is10Bit = pix.Contains("10", StringComparison.Ordinal) || pix.Contains("12", StringComparison.Ordinal);
                    var transfer = Str(s, "color_transfer") ?? string.Empty;
                    result.IsHdr = transfer is "smpte2084" or "arib-std-b67";
                }
                else if (type == "audio" && !result.HasAudio)
                {
                    result.HasAudio = true;
                    result.AudioCodec = Str(s, "codec_name");
                }
            }
        }

        return result;
    }

    private static bool IsAttachedPic(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Measures integrated loudness (LUFS) and true peak with ffmpeg's loudnorm filter.</summary>
    private async Task<(double? Loudness, double? TruePeak)> MeasureLoudnessAsync(string path, double target, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-i", path, "-map", "0:a:0", "-vn",
            "-af", string.Create(CultureInfo.InvariantCulture, $"loudnorm=I={target:0.0}:TP=-1:LRA=11:print_format=json"),
            "-f", "null", "-",
        };
        var (code, err) = await RunAsync(_mediaEncoder.EncoderPath, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (code != 0)
        {
            return (null, null);
        }

        return (ParseLoudnorm(err, "input_i"), ParseLoudnorm(err, "input_tp"));
    }

    /// <summary>Reads a value from loudnorm's JSON summary, e.g. "input_i" : "-23.54".</summary>
    public static double? ParseLoudnorm(string ffmpegOutput, string field)
    {
        var m = Regex.Match(ffmpegOutput ?? string.Empty, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([-+0-9.inf]+)\"");
        if (!m.Success)
        {
            return null;
        }

        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsInfinity(v) ? v : null;
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, List<string> args, TimeSpan timeout, CancellationToken ct, bool readStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Not allowed on some systems; harmless.
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return (-1, "timed out");
        }

        var output = readStdout ? await stdout.ConfigureAwait(false) : await stderr.ConfigureAwait(false);
        return (process.ExitCode, output);
    }

    private static string Signature()
    {
        var c = PrerollsPlugin.Instance.Configuration;
        return string.Create(CultureInfo.InvariantCulture, $"v2|{c.ConvertProblemFiles}|{c.LevelLoudness}|{c.TargetLoudness:0.0}");
    }

    private static bool SourceUnchanged(PrepRecord r)
    {
        var info = new FileInfo(r.SourcePath);
        return info.Exists && info.Length == r.SourceSize && info.LastWriteTimeUtc.Ticks == r.SourceModifiedTicks;
    }

    private void DeleteCopy(PrepRecord r)
    {
        if (r?.PreparedPath != null)
        {
            TryDelete(r.PreparedPath);
            r.PreparedPath = null;
        }
    }

    /// <summary>Deletes files in the cache folder that no record points to (e.g. after settings changed).</summary>
    private void CleanOrphans()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        var keep = new HashSet<string>(_records.Values.Where(r => r.PreparedPath != null).Select(r => r.PreparedPath), StringComparer.OrdinalIgnoreCase) { _stateFile };
        foreach (var f in Directory.EnumerateFiles(_dir))
        {
            if (!keep.Contains(f))
            {
                TryDelete(f);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

    private static string LastLines(string s, int n) =>
        string.Join(" ", (s ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));

    private void Load()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                foreach (var r in JsonSerializer.Deserialize<List<PrepRecord>>(File.ReadAllText(_stateFile)) ?? new List<PrepRecord>())
                {
                    if (r.Status == "Working")
                    {
                        r.Status = "Waiting";
                    }

                    _records[r.Key] = r;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable {File}", _stateFile);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_records.Values.ToList()));
            File.Move(tmp, _stateFile, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save {File}", _stateFile);
        }
    }
}
