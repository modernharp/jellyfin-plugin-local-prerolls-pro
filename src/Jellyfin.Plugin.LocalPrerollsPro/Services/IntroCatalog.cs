using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

public sealed class IntroFile
{
    public string Key { get; init; }

    public string Name { get; init; }

    public string Path { get; init; }

    public string Folder { get; init; }

    public Guid ItemId { get; init; }

    /// <summary>Settings from the intro's sidecar file and its folder's prerolls.json.</summary>
    public IntroMetaPatch Sidecar { get; init; }
}

public sealed class ScanReport
{
    public DateTime FinishedUtc { get; init; }

    public int Files { get; init; }

    public int Added { get; init; }

    public int Removed { get; init; }

    public List<string> Problems { get; init; } = new();
}

/// <summary>
/// Finds intro files in the configured folders and keeps a matching hidden library item for each,
/// because Jellyfin can only stream files it knows as items.
/// </summary>
public sealed class IntroCatalog
{
    /// <summary>Provider id that marks items for files in the intro folders.</summary>
    public const string ProviderKey = "lprp.video";

    /// <summary>Provider id for other intro items: prepared copies and per-title intros. Scans leave these alone.</summary>
    public const string ExtraProviderKey = "lprp.extra";

    /// <summary>Raised after every scan.</summary>
    public event EventHandler<ScanReport> Scanned;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<IntroCatalog> _logger;
    private readonly PlaybackHistory _history;
    private readonly ILocalizationManager _localization;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _itemLock = new();

    private volatile IReadOnlyDictionary<string, IntroFile> _files = new Dictionary<string, IntroFile>(StringComparer.OrdinalIgnoreCase);

    public IntroCatalog(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PlaybackHistory history, ILocalizationManager localization, ILogger<IntroCatalog> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _history = history;
        _localization = localization;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Full settings for an intro: sidecar file, then the settings page, plus first-seen date and rating score.
    /// </summary>
    public IntroMeta Meta(string key)
    {
        if (key == null || !_files.TryGetValue(key, out var file))
        {
            return null;
        }

        var page = PrerollsPlugin.Instance.Configuration.FileSettings
            .FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        var meta = IntroMetaMerge.Merge(file.Sidecar, page == null ? null : new IntroMetaPatch
        {
            Name = page.Name,
            Tags = page.Tags,
            Weight = page.Weight,
            Rating = string.IsNullOrWhiteSpace(page.Rating) ? null : page.Rating,
            From = string.IsNullOrWhiteSpace(page.From) ? null : page.From,
            To = string.IsNullOrWhiteSpace(page.To) ? null : page.To,
            Enabled = page.Enabled,
        });
        meta.Name ??= file.Name;
        meta.FirstSeenUtc = _history.FirstSeenUtc(key);
        meta.RatingScore = RatingScore(meta.Rating);
        return meta;
    }

    /// <summary>Comparable score for a rating such as "PG-13" or "TV-MA". Null when unrated or unknown.</summary>
    public int? RatingScore(string rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        try
        {
            var score = _localization.GetRatingScore(rating.Trim(), null);
            return score == null ? null : IntroEligibility.Combine(score.Score, score.SubScore);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unknown rating {Rating}", rating);
            return null;
        }
    }

    /// <summary>
    /// Returns the library item id for a video outside the intro folders (e.g. a per-title intro),
    /// creating a hidden item the first time.
    /// </summary>
    /// <param name="path">Video file.</param>
    /// <param name="name">Name shown while it plays; defaults to one made from the file name.</param>
    public Guid EnsureItem(string path, string name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? IntroKeys.DisplayName(path) : name.Trim();
        lock (_itemLock)
        {
            var id = _libraryManager.GetNewItemId(path, typeof(Video));
            var item = _libraryManager.GetItemById(id);
            if (item != null)
            {
                SetName(item, name);
            }
            else
            {
                item = new Video
                {
                    Id = id,
                    Path = path,
                    Name = name,
                    LockedFields = new[] { MetadataField.Name },
                    ProviderIds = new Dictionary<string, string> { { ExtraProviderKey, path } },
                };
                _libraryManager.CreateItem(item, null);
                QueueProbe(id);
                _logger.LogInformation("Added intro item {Path}", path);
            }

            return id;
        }
    }

    /// <summary>
    /// Sets the name viewers see while the intro plays, and locks it so Jellyfin's media probe
    /// doesn't replace it with a title embedded in the file.
    /// </summary>
    private void SetName(BaseItem item, string name)
    {
        var locked = item.LockedFields ?? Array.Empty<MetadataField>();
        if (item.Name == name && locked.Contains(MetadataField.Name))
        {
            return;
        }

        item.Name = name;
        if (!locked.Contains(MetadataField.Name))
        {
            item.LockedFields = locked.Append(MetadataField.Name).ToArray();
        }

        try
        {
            item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not rename intro item {Path}", item.Path);
        }
    }

    /// <summary>Reads sidecar settings: "file.mp4.json" or "file.json", over the folder's "prerolls.json".</summary>
    public IntroMetaPatch ReadSidecar(string videoPath, List<string> problems = null)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var fileName = Path.GetFileName(videoPath);
        IntroMetaPatch folderDefaults = null, folderEntry = null, own = null;

        var folderFile = Path.Combine(dir, "prerolls.json");
        if (File.Exists(folderFile))
        {
            var folder = ReadJson<FolderSidecar>(folderFile, problems);
            folderDefaults = folder?.Defaults;
            if (folder?.Files != null)
            {
                folderEntry = folder.Files.FirstOrDefault(kv => string.Equals(kv.Key, fileName, StringComparison.OrdinalIgnoreCase)).Value;
            }
        }

        foreach (var candidate in new[] { videoPath + ".json", Path.Combine(dir, Path.GetFileNameWithoutExtension(videoPath) + ".json") })
        {
            if (File.Exists(candidate))
            {
                own = ReadJson<IntroMetaPatch>(candidate, problems);
                break;
            }
        }

        if (folderDefaults == null && folderEntry == null && own == null)
        {
            return null;
        }

        var merged = IntroMetaMerge.Merge(folderDefaults, folderEntry, own);
        return new IntroMetaPatch
        {
            Name = merged.Name,
            Tags = merged.Tags.ToList(),
            Weight = merged.Weight,
            Rating = merged.Rating,
            From = merged.From,
            To = merged.To,
            Enabled = merged.Enabled,
            Credit = merged.Credit,
            Url = merged.SourceUrl,
        };
    }

    private T ReadJson<T>(string path, List<string> problems)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            var reason = ex is JsonException je && je.LineNumber.HasValue
                ? $"line {je.LineNumber + 1}: it isn't valid JSON (check commas, quotes and brackets), or a value has the wrong type"
                : ex.Message;
            var message = $"Ignored settings file {path}: {reason}.";
            if (problems != null && !problems.Contains(message))
            {
                problems.Add(message);
            }

            _logger.LogWarning("{Message}", message);
            return null;
        }
    }

    private sealed class FolderSidecar
    {
        public IntroMetaPatch Defaults { get; set; }

        public Dictionary<string, IntroMetaPatch> Files { get; set; }
    }

    /// <summary>Intro files found by the last scan, by key.</summary>
    public IReadOnlyDictionary<string, IntroFile> Files => _files;

    public ScanReport LastScan { get; private set; }

    public bool HasScanned => LastScan != null;

    public IntroFile FindByPath(string path) =>
        path == null ? null : _files.Values.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    public static bool IsIntroItem(BaseItem item) =>
        item?.ProviderIds != null && (item.ProviderIds.ContainsKey(ProviderKey) || item.ProviderIds.ContainsKey(ExtraProviderKey));

    public List<BaseItem> GetIntroItems() =>
        _libraryManager.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { { ProviderKey, string.Empty } },
        }).ToList();

    public async Task<ScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Scan(cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Removes every intro item from the library, then scans again. Rules are kept.</summary>
    public async Task<ScanReport> RebuildAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var extras = _libraryManager.GetItemList(new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { { ExtraProviderKey, string.Empty } },
            });
            foreach (var item in GetIntroItems().Concat(extras))
            {
                _logger.LogInformation("Removing intro item {Path} from the library", item.Path);
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            }

            return Scan(cancellationToken);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private ScanReport Scan(CancellationToken cancellationToken)
    {
        var config = PrerollsPlugin.Instance.Configuration;
        var problems = new List<string>();
        var extensions = IntroKeys.ParseExtensions(config.VideoExtensions);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // key -> path
        var missingRoots = new List<string>();
        var roots = config.IntroFolders.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();

        if (roots.Count == 0)
        {
            problems.Add("No pre-roll folder is set. Add one on the Files tab.");
        }

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> paths;
            if (File.Exists(root))
            {
                paths = new[] { root };
            }
            else if (Directory.Exists(root))
            {
                try
                {
                    paths = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = config.ScanSubfolders,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                    }).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Cannot read folder {root}: {ex.Message}");
                    missingRoots.Add(root);
                    continue;
                }
            }
            else
            {
                problems.Add($"Folder not found: {root}. Use the full path as the Jellyfin server sees it (with Docker, the path inside the container).");
                missingRoots.Add(root);
                continue;
            }

            var before = found.Count;
            foreach (var path in paths)
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith('.') || !extensions.Contains(Path.GetExtension(path)))
                {
                    continue; // Skips .nfo, images, subtitles, Thumbs.db, macOS "._" files, ...
                }

                var key = IntroKeys.FromPath(path, new[] { root });
                if (!found.TryAdd(key, path))
                {
                    problems.Add($"Skipped {path}: another pre-roll folder already has a file at \"{key}\". Rename one of them.");
                }
            }

            if (found.Count == before)
            {
                problems.Add($"No videos found in {root} (looking for {string.Join(" ", extensions)}).");
            }
        }

        // Sync library items with the files on disk.
        var existing = GetIntroItems();
        var byPath = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var item in existing)
        {
            if (string.IsNullOrEmpty(item.Path) || !byPath.TryAdd(item.Path, item))
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }); // Duplicate left by v1.
                removed++;
            }
        }

        var wantedPaths = new HashSet<string>(found.Values, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var files = new Dictionary<string, IntroFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, path) in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sidecar = ReadSidecar(path, problems);
            var baseName = !string.IsNullOrWhiteSpace(sidecar?.Name) ? sidecar.Name.Trim() : IntroKeys.DisplayName(path);
            var pageName = config.FileSettings.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase))?.Name;
            var shownName = string.IsNullOrWhiteSpace(pageName) ? baseName : pageName.Trim();

            if (!byPath.TryGetValue(path, out var item))
            {
                var id = _libraryManager.GetNewItemId(path, typeof(Video));
                item = _libraryManager.GetItemById(id);
                if (item == null)
                {
                    item = new Video
                    {
                        Id = id,
                        Path = path,
                        Name = shownName,
                        LockedFields = new[] { MetadataField.Name },
                        ProviderIds = new Dictionary<string, string> { { ProviderKey, path } },
                    };
                    _libraryManager.CreateItem(item, null);
                    added++;
                    _logger.LogInformation("Added intro {Path}", path);
                }
            }

            SetName(item, shownName);
            if (item.RunTimeTicks == null)
            {
                QueueProbe(item.Id); // Reads duration and codecs so the Files tab can flag problems.
            }

            files[key] = new IntroFile
            {
                Key = key,
                Name = baseName,
                Path = path,
                Folder = IntroKeys.FolderOf(key),
                ItemId = item.Id,
                Sidecar = sidecar,
            };
        }

        foreach (var (path, item) in byPath)
        {
            if (wantedPaths.Contains(path))
            {
                continue;
            }

            // Keep items under a folder that is offline right now (e.g. NAS asleep).
            if (missingRoots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _logger.LogInformation("Removing intro {Path}: file no longer in an intro folder", path);
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
            removed++;
        }

        _files = files;
        _history.MarkSeen(files.Keys, DateTime.UtcNow);
        var report = new ScanReport
        {
            FinishedUtc = DateTime.UtcNow,
            Files = files.Count,
            Added = added,
            Removed = removed,
            Problems = problems,
        };
        LastScan = report;

        _logger.LogInformation("Intro scan finished: {Files} intros, {Added} added, {Removed} removed", files.Count, added, removed);
        foreach (var p in problems)
        {
            _logger.LogWarning("Intro scan: {Problem}", p);
        }

        try
        {
            Scanned?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error after intro scan");
        }

        return report;
    }

    private void QueueProbe(Guid id)
    {
        try
        {
            _providerManager.QueueRefresh(id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not queue media probe for intro {Id}", id);
        }
    }
}
