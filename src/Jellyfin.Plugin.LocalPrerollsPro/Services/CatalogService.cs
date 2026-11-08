using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

/// <summary>One preroll from the prerolls.me public API (only the fields we use).</summary>
public sealed class CatalogItem
{
    public string Id { get; set; }

    public string Slug { get; set; }

    public string Title { get; set; }

    public string Description { get; set; }

    public string Url { get; set; }

    public List<string> Platforms { get; set; } = new();

    public CatalogRef Category { get; set; }

    public List<CatalogRef> Tags { get; set; } = new();

    /// <summary>"r2" (hosted MP4), "youtube" or "vimeo".</summary>
    public string Source { get; set; }

    public double? DurationSeconds { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public long? FileSize { get; set; }

    public CatalogThumb Thumbnail { get; set; }

    public CatalogVideo Video { get; set; }

    public CatalogDownload Download { get; set; }

    /// <summary>Who made it. The API may send a name or an object.</summary>
    public JsonElement Credit { get; set; }

    public CatalogStats Stats { get; set; }

    public DateTime? PublishedAt { get; set; }

    [JsonIgnore]
    public string CreditName => Credit.ValueKind switch
    {
        JsonValueKind.String => Credit.GetString(),
        JsonValueKind.Object when Credit.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
        _ => null,
    };

    [JsonIgnore]
    public bool IsDownloadable => Download?.IsDirectFile == true && !string.IsNullOrEmpty(Download.Url);
}

public sealed class CatalogRef
{
    public string Slug { get; set; }

    public string Name { get; set; }

    public int? Count { get; set; }
}

public sealed class CatalogThumb
{
    public string Small { get; set; }

    public string Large { get; set; }
}

public sealed class CatalogVideo
{
    public string DirectUrl { get; set; }
}

public sealed class CatalogDownload
{
    public string Url { get; set; }

    public bool IsDirectFile { get; set; }
}

public sealed class CatalogStats
{
    public long Downloads { get; set; }

    public long Likes { get; set; }
}

public sealed class CatalogFile
{
    public int Version { get; set; }

    public DateTime? GeneratedAt { get; set; }

    public int Count { get; set; }

    public List<CatalogRef> Categories { get; set; } = new();

    public List<CatalogRef> Tags { get; set; } = new();

    public List<CatalogItem> Prerolls { get; set; } = new();
}

/// <summary>How often the plugin has contacted prerolls.me since the server started.</summary>
public sealed class CatalogTraffic
{
    public int CatalogFetches { get; set; }

    public int ThumbnailFetches { get; set; }

    public int Downloads { get; set; }

    public DateTime? LastCatalogFetchUtc { get; set; }

    public DateTime? LastFailureUtc { get; set; }

    public string LastError { get; set; }
}

public sealed class DownloadResult
{
    public string Slug { get; init; }

    public bool Ok { get; init; }

    public string Message { get; init; }

    public string Key { get; init; }
}

/// <summary>
/// Reads the prerolls.me public catalog and downloads hosted prerolls into an intro folder.
/// Follows the site's fair-use notes: the static catalog is fetched at most hourly, downloads go
/// through download.url (which counts them), and credit and source links are kept with each file.
/// </summary>
public sealed class CatalogService
{
    public const string BaseUrl = "https://www.prerolls.me";
    private const string UserAgent = "LocalPreRollsPro/1.0 (Jellyfin plugin; +https://www.prerolls.me/developers)";
    private const long MaxDownloadBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromHours(1);

    /// <summary>A "refresh now" is ignored if the catalog was fetched more recently than this.</summary>
    private static readonly TimeSpan ForcedRefreshMinAge = TimeSpan.FromMinutes(10);

    /// <summary>After a failed fetch, wait this long before contacting the site again.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Most downloads one subscription sync may make; the rest follow on the next run.</summary>
    public const int MaxDownloadsPerSync = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    // Thumbnails come from prerolls.me, or from YouTube's and Vimeo's image servers for clips hosted there.
    private static readonly HashSet<string> AllowedThumbHosts = new(StringComparer.OrdinalIgnoreCase) { "videos.prerolls.me", "www.prerolls.me", "prerolls.me", "i.ytimg.com", "img.youtube.com", "i.vimeocdn.com" };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IntroCatalog _catalog;
    private readonly PlaybackHistory _history;
    private readonly ILogger<CatalogService> _logger;
    private readonly string _dir;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _thumbLock = new(4, 4);
    private readonly Dictionary<string, DateTime> _thumbFailures = new();
    private CatalogFile _cached;
    private DateTime _cachedAtUtc;

    public CatalogTraffic Traffic { get; } = new();

    public CatalogService(IHttpClientFactory httpFactory, IntroCatalog catalog, PlaybackHistory history, IApplicationPaths paths, ILogger<CatalogService> logger)
    {
        _httpFactory = httpFactory;
        _catalog = catalog;
        _history = history;
        _logger = logger;
        _dir = Path.Combine(paths.CachePath, "local-prerolls-pro-catalog");
    }

    /// <summary>The catalog, from memory, disk or the network (at most hourly unless forced).</summary>
    public async Task<CatalogFile> GetCatalogAsync(bool force, CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = Path.Combine(_dir, "catalog.json");
            if (_cached == null && File.Exists(file))
            {
                _cached = Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                _cachedAtUtc = File.GetLastWriteTimeUtc(file);
            }

            var age = DateTime.UtcNow - _cachedAtUtc;
            if (_cached != null && (age < (force ? ForcedRefreshMinAge : CatalogMaxAge)))
            {
                return _cached; // Fresh enough; don't contact the site.
            }

            if (Traffic.LastFailureUtc is { } failed && DateTime.UtcNow - failed < FailureBackoff)
            {
                if (_cached != null)
                {
                    return _cached;
                }

                throw new CatalogUnavailableException(
                    $"prerolls.me could not be reached a moment ago ({Traffic.LastError}). The plugin waits {FailureBackoff.TotalMinutes:0} minutes before trying again, so please try later.");
            }

            try
            {
                using var client = Client();
                Traffic.CatalogFetches++;
                Traffic.LastCatalogFetchUtc = DateTime.UtcNow;
                var json = await client.GetStringAsync(BaseUrl + "/api/v1/catalog.json", ct).ConfigureAwait(false);
                var parsed = Parse(json);
                Directory.CreateDirectory(_dir);
                await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);
                _cached = parsed;
                _cachedAtUtc = DateTime.UtcNow;
                Traffic.LastFailureUtc = null;
                Traffic.LastError = null;
                _logger.LogInformation("Loaded prerolls.me catalog: {Count} prerolls", parsed.Prerolls.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                Traffic.LastFailureUtc = DateTime.UtcNow;
                Traffic.LastError = ex is TaskCanceledException ? "timed out" : ex.Message;
                _logger.LogWarning("Could not load the prerolls.me catalog: {Message}", Traffic.LastError);
                if (_cached == null)
                {
                    throw new CatalogUnavailableException($"prerolls.me could not be reached ({Traffic.LastError}). Check that the Jellyfin server has internet access, then try again in a few minutes.");
                }
            }

            return _cached ?? throw new InvalidOperationException("The prerolls.me catalog could not be loaded.");
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public DateTime CachedAtUtc => _cachedAtUtc;

    /// <summary>Downloads a hosted preroll into the intro folder, with a sidecar file holding its tags and credit.</summary>
    public async Task<DownloadResult> DownloadAsync(string slug, bool rescan, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(false, ct).ConfigureAwait(false);
        var item = catalog.Prerolls.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return Fail(slug, "Not found in the catalog.");
        }

        if (!item.IsDownloadable)
        {
            return Fail(slug, $"\"{item.Title}\" is hosted on {item.Source}; open it on prerolls.me instead.");
        }

        var (folder, folderError) = DownloadFolder(item);
        if (folder == null)
        {
            return Fail(slug, folderError);
        }

        var target = Path.Combine(folder, SafeName(item.Slug) + ".mp4");
        await _downloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(folder);
                var tmp = target + ".part";
                using var client = Client();
                Traffic.Downloads++;
                using (var response = await client.GetAsync(item.Download.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (!type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && type != "application/octet-stream")
                    {
                        return Fail(slug, $"prerolls.me returned {type} instead of a video.");
                    }

                    if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    {
                        return Fail(slug, "File is larger than 500 MB.");
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using (var output = File.Create(tmp))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxDownloadBytes)
                            {
                                throw new InvalidOperationException("File is larger than 500 MB.");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                    }
                }

                File.Move(tmp, target, true);
                _logger.LogInformation("Downloaded prerolls.me \"{Title}\" to {Path}", item.Title, target);
            }

            await WriteSidecarAsync(target, item, ct).ConfigureAwait(false);
            _history.MarkDownloaded(item.Slug);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TryDelete(target + ".part");
            return Fail(slug, ex.Message);
        }
        finally
        {
            _downloadLock.Release();
        }

        if (rescan)
        {
            await _catalog.ScanAsync(ct).ConfigureAwait(false);
        }

        var key = IntroKeys.FromPath(target, PrerollsPlugin.Instance.Configuration.IntroFolders);
        return new DownloadResult { Slug = slug, Ok = true, Key = key, Message = $"Downloaded \"{item.Title}\"." };
    }

    /// <summary>Downloads new hosted prerolls for every enabled subscription.</summary>
    public async Task<List<DownloadResult>> SyncAsync(bool force, CancellationToken ct)
    {
        var results = new List<DownloadResult>();
        var config = PrerollsPlugin.Instance.Configuration;
        var subs = config.CatalogSubscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Value)).ToList();
        if (!config.CatalogEnabled || subs.Count == 0)
        {
            return results;
        }

        if (!await _syncLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return results; // A sync is already running.
        }

        try
        {
            var catalog = await GetCatalogAsync(force, ct).ConfigureAwait(false);
            foreach (var item in catalog.Prerolls.Where(p => p.IsDownloadable && subs.Any(s => Matches(p, s))))
            {
                ct.ThrowIfCancellationRequested();
                if (_history.WasDownloaded(item.Slug))
                {
                    continue; // Downloaded before; if the file was deleted, that was on purpose.
                }

                if (results.Count >= MaxDownloadsPerSync)
                {
                    break; // Be gentle with the site; the next run continues.
                }

                results.Add(await DownloadAsync(item.Slug, rescan: false, ct).ConfigureAwait(false));
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            if (results.Any(r => r.Ok))
            {
                await _catalog.ScanAsync(ct).ConfigureAwait(false);
            }

            return results;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public static bool Matches(CatalogItem item, CatalogSubscription sub)
    {
        if (!string.IsNullOrWhiteSpace(sub.Platform) && !item.Platforms.Contains(sub.Platform.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var v = sub.Value.Trim();
        return sub.Kind?.ToLowerInvariant() switch
        {
            "tag" => item.Tags.Any(t => string.Equals(t.Slug, v, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, v, StringComparison.OrdinalIgnoreCase)),
            "platform" => item.Platforms.Contains(v, StringComparer.OrdinalIgnoreCase),
            _ => item.Category != null && (string.Equals(item.Category.Slug, v, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Category.Name, v, StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>Whether a preroll's file is already in the download folder.</summary>
    public bool IsOnDisk(CatalogItem item)
    {
        var (folder, _) = DownloadFolder(item);
        return folder != null && File.Exists(Path.Combine(folder, SafeName(item.Slug) + ".mp4"));
    }

    /// <summary>Fetches a thumbnail image from prerolls.me (cached on disk). Null if not allowed or missing.</summary>
    public async Task<(byte[] Data, string ContentType)> ThumbnailAsync(string id, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(false, ct).ConfigureAwait(false);
        var item = catalog.Prerolls.FirstOrDefault(p => p.Id == id);
        var url = item?.Thumbnail?.Small;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !AllowedThumbHosts.Contains(uri.Host))
        {
            return (null, null);
        }

        var ext = Path.GetExtension(uri.AbsolutePath);
        var file = Path.Combine(_dir, "thumbs", SafeName(id) + (string.IsNullOrEmpty(ext) ? ".img" : ext));
        var type = ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        if (File.Exists(file))
        {
            return (await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), type);
        }

        lock (_thumbFailures)
        {
            if (_thumbFailures.TryGetValue(id, out var failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromHours(1))
            {
                return (null, null); // Failed recently; don't ask again yet.
            }
        }

        await _thumbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(file))
            {
                return (await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), type); // Another request just fetched it.
            }

            using var client = Client();
            Traffic.ThumbnailFetches++;
            var data = await client.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, data, ct).ConfigureAwait(false);
            return (data, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            lock (_thumbFailures)
            {
                _thumbFailures[id] = DateTime.UtcNow;
            }

            return (null, null);
        }
        finally
        {
            _thumbLock.Release();
        }
    }

    /// <summary>Where a preroll is saved: the download folder (or first intro folder), plus a category subfolder if set.</summary>
    public (string Folder, string Error) DownloadFolder(CatalogItem item)
    {
        var config = PrerollsPlugin.Instance.Configuration;
        var roots = config.IntroFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f.Trim())).Select(f => f.Trim()).ToList();
        var baseFolder = string.IsNullOrWhiteSpace(config.CatalogDownloadFolder) ? roots.FirstOrDefault() : config.CatalogDownloadFolder.Trim();
        if (baseFolder == null)
        {
            return (null, "Add a pre-roll folder on the Files tab first; downloads are saved there.");
        }

        if (!roots.Any(r => IsInside(baseFolder, r)))
        {
            return (null, $"The download folder {baseFolder} is not inside one of your pre-roll folders, so downloads would never play. Change it on the Catalog tab.");
        }

        var folder = config.CatalogFolderPerCategory && item?.Category?.Slug != null
            ? Path.Combine(baseFolder, SafeName(item.Category.Slug))
            : baseFolder;
        return (folder, null);
    }

    private static bool IsInside(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    private static async Task WriteSidecarAsync(string videoPath, CatalogItem item, CancellationToken ct)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Category?.Name))
        {
            tags.Add(item.Category.Name);
        }

        tags.AddRange(item.Tags.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        tags.Add("prerolls.me");
        var sidecar = new IntroMetaPatch
        {
            Name = item.Title,
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Credit = item.CreditName ?? "prerolls.me",
            Url = item.Url,
        };
        var json = JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(videoPath + ".json", json, ct).ConfigureAwait(false);
    }

    /// <summary>True while a subscription sync is running.</summary>
    public bool Syncing => _syncLock.CurrentCount == 0;

    private HttpClient Client()
    {
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static CatalogFile Parse(string json) =>
        JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions) ?? throw new JsonException("Empty catalog");

    public static string SafeName(string s)
    {
        var cleaned = Regex.Replace(s ?? string.Empty, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return cleaned.Length == 0 ? "preroll" : cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static DownloadResult Fail(string slug, string message) => new() { Slug = slug, Ok = false, Message = message };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>prerolls.me can't be reached; the message is shown to the user as is.</summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message)
        : base(message)
    {
    }
}
