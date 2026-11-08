using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LocalPrerollsPro.API;

/// <summary>Admin-only endpoints used by the settings page.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("LocalPrerollsPro")]
[Produces(MediaTypeNames.Application.Json)]
public class PrerollsController : ControllerBase
{
    private readonly IntroCatalog _catalog;
    private readonly PlaybackHistory _history;
    private readonly PreShowBuilder _builder;
    private readonly IntroPreparer _preparer;
    private readonly CatalogService _prerollsMe;
    private readonly LocalIntrosImporter _importer;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PrerollsController(IntroCatalog catalog, PlaybackHistory history, PreShowBuilder builder, IntroPreparer preparer, CatalogService prerollsMe, LocalIntrosImporter importer, ILibraryManager libraryManager, IUserManager userManager)
    {
        _catalog = catalog;
        _history = history;
        _builder = builder;
        _preparer = preparer;
        _prerollsMe = prerollsMe;
        _importer = importer;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>Scans the intro folders now.</summary>
    [HttpPost("Scan")]
    public async Task<ActionResult<ScanReport>> Scan(CancellationToken cancellationToken) =>
        await _catalog.ScanAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Removes all intro items from the library and scans again. Rules are kept.</summary>
    [HttpPost("Rebuild")]
    public async Task<ActionResult<ScanReport>> Rebuild(CancellationToken cancellationToken) =>
        await _catalog.RebuildAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Re-checks every intro for conversion and loudness.</summary>
    [HttpPost("Prepare")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Prepare()
    {
        _preparer.Trigger();
        return NoContent();
    }

    /// <summary>Intro files with media details, settings, conversion status, loudness and play counts.</summary>
    [HttpGet("Files")]
    public ActionResult<object> Files()
    {
        var config = PrerollsPlugin.Instance.Configuration;
        var stats = _history.StatsSnapshot();
        var files = _catalog.Files.Values.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var item = _libraryManager.GetItemById(f.ItemId);
            var streams = item?.GetMediaStreams() ?? new List<MediaStream>();
            var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var audio = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
            var meta = _catalog.Meta(f.Key);
            var prep = _preparer.Record(f.Key);
            stats.TryGetValue(f.Key, out var s);

            var notes = new List<string>();
            if (prep?.Problems?.Count > 0)
            {
                notes.Add((config.ConvertProblemFiles ? "Converted copy " + (prep.Status == "Ready" ? "is used" : "pending") : "Some clients will transcode it") + ": " + string.Join(", ", prep.Problems) + ".");
            }

            var levelled = prep?.Status == "Ready" && (prep.PreparedFor ?? string.Empty).Contains("volume", StringComparison.Ordinal);
            if (prep?.Loudness is double lufs && Math.Abs(lufs - config.TargetLoudness) > 4 && !levelled)
            {
                var diff = lufs - config.TargetLoudness;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(diff):0} dB {(diff > 0 ? "louder" : "quieter")} than the target ({config.TargetLoudness:0} LUFS).") + (config.LevelLoudness ? string.Empty : " Turn on volume levelling to fix it."));
            }

            if (!string.IsNullOrWhiteSpace(meta?.Rating) && meta.RatingScore == null)
            {
                notes.Add($"Rating \"{meta.Rating}\" isn't one Jellyfin knows, so age-safe treats this pre-roll as unrated. Pick a rating with Edit.");
            }

            if (prep?.Status == "Failed")
            {
                notes.Add("Preparing failed: " + prep.Error);
            }

            return new
            {
                f.Key,
                Name = meta?.Name ?? f.Name,
                FileName = f.Name,
                f.Folder,
                f.Path,
                ItemId = f.ItemId.ToString("N"),
                DurationSeconds = item?.RunTimeTicks is long t ? Math.Round(TimeSpan.FromTicks(t).TotalSeconds, 1) : (double?)null,
                Video = video == null ? null : $"{video.Codec} {video.Width}x{video.Height}".Trim(),
                Audio = audio == null ? null : $"{audio.Codec} {audio.Channels}ch".Trim(),
                Loudness = prep?.Loudness,
                PrepStatus = prep?.Status ?? "Waiting",
                PreparedFor = prep?.Status == "Ready" ? prep.PreparedFor : null,
                Notes = notes,
                Tags = meta?.Tags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
                Rating = meta?.Rating,
                Weight = meta?.Weight ?? 1,
                From = meta?.From,
                To = meta?.To,
                Enabled = meta?.Enabled ?? true,
                Credit = meta?.Credit,
                SourceUrl = meta?.SourceUrl,
                IsNew = meta?.FirstSeenUtc is DateTime seen && config.BoostNewDays > 0 && DateTime.UtcNow - seen < TimeSpan.FromDays(config.BoostNewDays),
                Sidecar = f.Sidecar != null,
                Plays = s?.Plays ?? 0,
                Picks = s?.Picks ?? 0,
                s?.LastPlayedUtc,
            };
        }).ToList();

        var folders = files.Select(f => f.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var tags = files.SelectMany(f => f.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return new { Files = files, Folders = folders, Tags = tags, LastScan = _catalog.LastScan, Preparing = _preparer.Busy };
    }

    /// <summary>Shows what would play before an item, without recording anything.</summary>
    [HttpGet("Simulate")]
    public ActionResult<object> Simulate([FromQuery] Guid itemId, [FromQuery] Guid? userId, [FromQuery] string at)
    {
        var item = itemId == Guid.Empty ? null : _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound(new ErrorMessage("That title is no longer in your library. Search for it again."));
        }

        var user = userId.HasValue && userId.Value != Guid.Empty ? _userManager.GetUserById(userId.Value) : null;
        if (userId.HasValue && userId.Value != Guid.Empty && user == null)
        {
            return NotFound(new ErrorMessage("That viewer no longer exists. Pick another one."));
        }

        DateTime when;
        if (string.IsNullOrWhiteSpace(at))
        {
            when = DateTime.Now;
        }
        else if (!DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when))
        {
            return BadRequest(new ErrorMessage($"\"{at}\" is not a date and time the plugin understands. Use the date picker."));
        }
        var plan = _builder.Build(item, user, when, record: false);
        var c = plan.Context;

        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id =>
            Guid.TryParse(id, out var g) ? _libraryManager.GetItemById(g)?.Name ?? id : id));

        return new
        {
            Item = new { item.Name, Type = item.GetBaseItemKind().ToString() },
            User = user?.Username,
            At = when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            plan.SkipReason,
            Intros = plan.Intros.Select(p => new { p.Name, p.Key, p.Source, p.Prepared }),
            Excluded = plan.Excluded.Select(kv => new { Name = _catalog.Meta(kv.Key)?.Name ?? kv.Key, Reason = kv.Value }),
            plan.TitleIntros,
            Steps = plan.Steps.Select(s => new
            {
                s.Index,
                s.Kind,
                s.Note,
                s.Picked,
                Rules = s.Selection?.Rules,
                Candidates = s.Selection?.Candidates.Select(x => new { x.Key, Name = _catalog.Meta(x.Key)?.Name ?? x.Key, x.Probability, x.RecentlyPlayed }),
            }),
            Facts = c == null ? null : new Dictionary<string, string>
            {
                ["Type"] = c.ItemType,
                ["Libraries"] = Names(c.LibraryIds),
                ["Genres"] = string.Join(", ", c.Genres),
                ["Tags"] = string.Join(", ", c.Tags),
                ["Studios"] = string.Join(", ", c.Studios),
                ["Collections"] = Names(c.CollectionIds),
                ["Rating"] = c.OfficialRating,
                ["Year"] = c.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["Premiere"] = c.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                ["Resolution"] = c.Resolution,
                ["Video range"] = string.Join(", ", c.VideoRanges),
                ["Audio"] = string.Join(", ", c.AudioFormats),
                ["Day"] = c.Now.DayOfWeek.ToString(),
            },
        };
    }

    /// <summary>Checks folder paths typed on the settings page: do they exist, and how many videos do they hold.</summary>
    [HttpPost("CheckFolders")]
    public ActionResult<IEnumerable<FolderCheck>> CheckFolders([FromBody] FolderCheckRequest request)
    {
        var config = PrerollsPlugin.Instance.Configuration;
        var extensions = IntroKeys.ParseExtensions(string.IsNullOrWhiteSpace(request?.Extensions) ? config.VideoExtensions : request.Extensions);
        var results = new List<FolderCheck>();
        foreach (var raw in (request?.Paths ?? new List<string>()).Take(50))
        {
            var path = (raw ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                continue;
            }

            if (!System.IO.Path.IsPathRooted(path))
            {
                results.Add(new FolderCheck { Path = path, Ok = false, Message = "Use a full path that starts at the root, e.g. /media/prerolls or D:\\Prerolls." });
                continue;
            }

            if (System.IO.File.Exists(path))
            {
                var isVideo = extensions.Contains(System.IO.Path.GetExtension(path));
                results.Add(new FolderCheck { Path = path, Ok = isVideo, Videos = isVideo ? 1 : 0, Message = isVideo ? "A single video file." : "This is a file, not a folder." });
                continue;
            }

            if (!System.IO.Directory.Exists(path))
            {
                results.Add(new FolderCheck { Path = path, Ok = false, Message = "Not found on the server. With Docker, use the path inside the container." });
                continue;
            }

            try
            {
                var count = System.IO.Directory.EnumerateFiles(path, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = config.ScanSubfolders, IgnoreInaccessible = true })
                    .Count(f => extensions.Contains(System.IO.Path.GetExtension(f)) && !System.IO.Path.GetFileName(f).StartsWith('.'));
                results.Add(new FolderCheck { Path = path, Ok = true, Videos = count, Message = count == 0 ? "Found, but it has no videos yet." : $"Found, {count} video{(count == 1 ? string.Empty : "s")}." });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                results.Add(new FolderCheck { Path = path, Ok = false, Message = "Jellyfin isn't allowed to read this folder. Check its permissions." });
            }
        }

        return results;
    }

    [HttpPost("Stats/Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ResetStats()
    {
        _history.ResetStats();
        _history.Flush();
        return NoContent();
    }

    /// <summary>Warnings for the top of the settings page.</summary>
    [HttpGet("Status")]
    public ActionResult<object> Status()
    {
        var import = _importer.Status();
        return new { Import = import, Catalog = _prerollsMe.Traffic, CatalogFetchedUtc = _prerollsMe.CachedAtUtc == default ? (DateTime?)null : _prerollsMe.CachedAtUtc, Syncing = _prerollsMe.Syncing };
    }

    /// <summary>Replaces this plugin's folders, rules and playback settings with Local Intros' ones, then scans.</summary>
    [HttpPost("Import")]
    public async Task<ActionResult<object>> Import(CancellationToken cancellationToken)
    {
        var plugin = PrerollsPlugin.Instance;
        try
        {
            var message = _importer.Import(plugin.Configuration);
            plugin.SaveConfiguration();
            await _catalog.ScanAsync(cancellationToken).ConfigureAwait(false);
            return new { Message = message };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorMessage(ex.Message));
        }
    }

    /// <summary>Searches the prerolls.me catalog (cached for an hour).</summary>
    [HttpGet("Catalog")]
    public async Task<ActionResult<object>> Catalog(
        [FromQuery] string q,
        [FromQuery] string platform,
        [FromQuery] string category,
        [FromQuery] string tag,
        [FromQuery] bool downloadableOnly = false,
        [FromQuery] string sort = "popular",
        [FromQuery] int page = 1,
        [FromQuery] int limit = 24,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        CatalogFile catalog;
        try
        {
            catalog = await _prerollsMe.GetCatalogAsync(refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (CatalogUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorMessage(ex.Message));
        }

        IEnumerable<CatalogItem> items = catalog.Prerolls;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(p => terms.All(t =>
                (p.Title ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)
                || (p.Category?.Name ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)
                || p.Tags.Any(x => (x.Name ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase))
                || (p.CreditName ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            items = items.Where(p => p.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            items = items.Where(p => string.Equals(p.Category?.Slug, category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            items = items.Where(p => p.Tags.Any(x => string.Equals(x.Slug, tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (downloadableOnly)
        {
            items = items.Where(p => p.IsDownloadable);
        }

        items = sort switch
        {
            "newest" => items.OrderByDescending(p => p.PublishedAt),
            "shortest" => items.OrderBy(p => p.DurationSeconds ?? double.MaxValue),
            "az" => items.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(p => p.Stats?.Downloads ?? 0).ThenByDescending(p => p.Stats?.Likes ?? 0),
        };

        var list = items.ToList();
        var folder = _prerollsMe.DownloadFolder(null);
        limit = Math.Clamp(limit, 1, 100);
        page = Math.Max(1, page);
        var pageItems = list.Skip((page - 1) * limit).Take(limit).Select(p => new
        {
            p.Id,
            p.Slug,
            p.Title,
            p.Description,
            p.Url,
            p.Platforms,
            Category = p.Category?.Name,
            CategorySlug = p.Category?.Slug,
            Tags = p.Tags.Select(t => t.Name),
            p.Source,
            p.DurationSeconds,
            Resolution = p.Width.HasValue ? $"{p.Width}x{p.Height}" : null,
            p.FileSize,
            Credit = p.CreditName,
            PreviewUrl = p.Video?.DirectUrl,
            p.IsDownloadable,
            Downloaded = p.IsDownloadable && (_prerollsMe.IsOnDisk(p) || _history.WasDownloaded(p.Slug)),
            OnDisk = p.IsDownloadable && _prerollsMe.IsOnDisk(p),
        }).ToList();

        return new
        {
            Total = list.Count,
            Page = page,
            Pages = (int)Math.Ceiling(list.Count / (double)limit),
            Items = pageItems,
            Categories = catalog.Categories,
            Tags = catalog.Tags.OrderByDescending(t => t.Count ?? 0).Take(80),
            catalog.GeneratedAt,
            FetchedUtc = _prerollsMe.CachedAtUtc,
            DownloadFolder = folder.Folder,
            DownloadFolderError = folder.Error,
        };
    }

    /// <summary>Thumbnail image for a catalog item (proxied and cached).</summary>
    [HttpGet("Catalog/Thumb/{id}")]
    [Produces("image/webp", "image/jpeg", "image/png")]
    public async Task<ActionResult> Thumb([FromRoute] string id, CancellationToken cancellationToken)
    {
        var (data, type) = await _prerollsMe.ThumbnailAsync(id, cancellationToken).ConfigureAwait(false);
        if (data == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=86400";
        return File(data, type);
    }

    /// <summary>Downloads a hosted preroll into the intro folder.</summary>
    [HttpPost("Catalog/Download/{slug}")]
    public async Task<ActionResult<DownloadResult>> Download([FromRoute] string slug, CancellationToken cancellationToken)
    {
        try
        {
            return await _prerollsMe.DownloadAsync(slug, rescan: true, cancellationToken).ConfigureAwait(false);
        }
        catch (CatalogUnavailableException ex)
        {
            return new DownloadResult { Slug = slug, Ok = false, Message = ex.Message };
        }
    }

    /// <summary>Downloads new prerolls for all subscriptions now.</summary>
    [HttpPost("Catalog/Sync")]
    public async Task<ActionResult<object>> Sync(CancellationToken cancellationToken)
    {
        var config = PrerollsPlugin.Instance.Configuration;
        if (!config.CatalogSubscriptions.Any(s => s.Enabled))
        {
            return BadRequest(new ErrorMessage("You aren't following anything yet. Pick a category or tag under Subscriptions and click Follow first."));
        }

        if (_prerollsMe.Syncing)
        {
            return Conflict(new ErrorMessage("A sync is already running. Wait for it to finish."));
        }

        try
        {
            var results = await _prerollsMe.SyncAsync(false, cancellationToken).ConfigureAwait(false);
            return new
            {
                Downloaded = results.Count(r => r.Ok),
                Failed = results.Where(r => !r.Ok).Select(r => r.Slug + ": " + r.Message),
                MoreLater = results.Count >= CatalogService.MaxDownloadsPerSync,
            };
        }
        catch (CatalogUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorMessage(ex.Message));
        }
    }
}

/// <summary>Error body the settings page shows to the user.</summary>
public sealed record ErrorMessage(string Message);

public sealed class FolderCheckRequest
{
    public List<string> Paths { get; set; } = new();

    public string Extensions { get; set; }
}

public sealed class FolderCheck
{
    public string Path { get; init; }

    public bool Ok { get; init; }

    public int Videos { get; init; }

    public string Message { get; init; }
}
