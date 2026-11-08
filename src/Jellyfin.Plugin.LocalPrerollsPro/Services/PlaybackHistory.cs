using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.LocalPrerollsPro.Engine;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

public sealed class IntroStats
{
    /// <summary>Times a client actually started playing the intro.</summary>
    public long Plays { get; set; }

    /// <summary>Times the plugin chose the intro.</summary>
    public long Picks { get; set; }

    public DateTime? LastPlayedUtc { get; set; }
}

public sealed class UserHistory
{
    public DateTime? LastIntroUtc { get; set; }

    /// <summary>Most recent first.</summary>
    public List<string> RecentKeys { get; set; } = new();

    /// <summary>Series id -> last time the user started or stopped an episode of it.</summary>
    public Dictionary<string, DateTime> SeriesActivityUtc { get; set; } = new();

    /// <summary>Series id -> last time an intro was given for it.</summary>
    public Dictionary<string, DateTime> SeriesIntroUtc { get; set; } = new();
}

public sealed class HistoryData
{
    public Dictionary<string, IntroStats> Intros { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, UserHistory> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Cursors { get; set; } = new();

    /// <summary>Intro key -> when a scan first found it.</summary>
    public Dictionary<string, DateTime> FirstSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>prerolls.me slugs already downloaded (so deleted files are not downloaded again).</summary>
    public HashSet<string> DownloadedSlugs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers what each user has seen (for no-repeat, cooldowns and binge detection)
/// and counts plays per intro. Saved to a JSON file next to the plugin settings.
/// </summary>
public sealed class PlaybackHistory : IDisposable
{
    private const int MaxRecent = 25;
    private static readonly TimeSpan KeepSeriesFor = TimeSpan.FromDays(3);

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<PlaybackHistory> _logger;
    private readonly Timer _saveTimer;
    private HistoryData _data;
    private bool _dirty;

    public PlaybackHistory(IApplicationPaths paths, ILogger<PlaybackHistory> logger)
        : this(Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.LocalPrerollsPro.history.json"), logger)
    {
    }

    public PlaybackHistory(string path, ILogger<PlaybackHistory> logger)
    {
        _path = path;
        _logger = logger;
        _data = Load();
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ISet<string> RecentKeys(string userId, int count)
    {
        lock (_lock)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (count > 0 && _data.Users.TryGetValue(Id(userId), out var u))
            {
                foreach (var k in u.RecentKeys.Take(count))
                {
                    set.Add(k);
                }
            }

            return set;
        }
    }

    public DateTime? LastIntroUtc(string userId)
    {
        lock (_lock)
        {
            return _data.Users.TryGetValue(Id(userId), out var u) ? u.LastIntroUtc : null;
        }
    }

    public DateTime? SeriesActivityUtc(string userId, string seriesId)
    {
        lock (_lock)
        {
            return _data.Users.TryGetValue(Id(userId), out var u) && u.SeriesActivityUtc.TryGetValue(Id(seriesId), out var t) ? t : null;
        }
    }

    public DateTime? SeriesIntroUtc(string userId, string seriesId)
    {
        lock (_lock)
        {
            return _data.Users.TryGetValue(Id(userId), out var u) && u.SeriesIntroUtc.TryGetValue(Id(seriesId), out var t) ? t : null;
        }
    }

    public long PlayCount(string key)
    {
        lock (_lock)
        {
            return _data.Intros.TryGetValue(key, out var s) ? s.Plays : 0;
        }
    }

    public string GetCursor(string signature)
    {
        lock (_lock)
        {
            return _data.Cursors.TryGetValue(signature, out var k) ? k : null;
        }
    }

    public void RecordPicks(string userId, IReadOnlyList<string> keys, string seriesId, IEnumerable<KeyValuePair<string, string>> cursorUpdates, DateTime utcNow)
    {
        lock (_lock)
        {
            var u = User(userId);
            if (keys.Count > 0)
            {
                u.LastIntroUtc = utcNow;
                if (!string.IsNullOrEmpty(seriesId))
                {
                    u.SeriesIntroUtc[Id(seriesId)] = utcNow;
                }
            }

            foreach (var key in keys)
            {
                u.RecentKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                u.RecentKeys.Insert(0, key);
                Stats(key).Picks++;
            }

            if (u.RecentKeys.Count > MaxRecent)
            {
                u.RecentKeys.RemoveRange(MaxRecent, u.RecentKeys.Count - MaxRecent);
            }

            foreach (var (sig, key) in cursorUpdates ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                _data.Cursors[sig] = key;
            }

            if (_data.Cursors.Count > 200)
            {
                _data.Cursors.Clear(); // Signatures go stale as rules change; dropping them only resets the rotation.
            }

            MarkDirty();
        }
    }

    public void RecordSeriesActivity(string userId, string seriesId, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(seriesId))
        {
            return;
        }

        lock (_lock)
        {
            var u = User(userId);
            u.SeriesActivityUtc[Id(seriesId)] = utcNow;
            Prune(u, utcNow);
            MarkDirty();
        }
    }

    public void RecordPlay(string key, DateTime utcNow)
    {
        lock (_lock)
        {
            var s = Stats(key);
            s.Plays++;
            s.LastPlayedUtc = utcNow;
            MarkDirty();
        }
    }

    public Dictionary<string, IntroStats> StatsSnapshot()
    {
        lock (_lock)
        {
            return _data.Intros.ToDictionary(
                kv => kv.Key,
                kv => new IntroStats { Plays = kv.Value.Plays, Picks = kv.Value.Picks, LastPlayedUtc = kv.Value.LastPlayedUtc },
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public DateTime? FirstSeenUtc(string key)
    {
        lock (_lock)
        {
            return _data.FirstSeen.TryGetValue(key, out var t) ? t : null;
        }
    }

    public void MarkSeen(IEnumerable<string> keys, DateTime utcNow)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var k in keys)
            {
                if (_data.FirstSeen.TryAdd(k, utcNow))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                MarkDirty();
            }
        }
    }

    public bool WasDownloaded(string slug)
    {
        lock (_lock)
        {
            return _data.DownloadedSlugs.Contains(slug);
        }
    }

    public void MarkDownloaded(string slug)
    {
        lock (_lock)
        {
            if (_data.DownloadedSlugs.Add(slug))
            {
                MarkDirty();
            }
        }
    }

    public void ResetStats()
    {
        lock (_lock)
        {
            _data.Intros.Clear();
            MarkDirty();
        }
    }

    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_data);
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save intro history to {Path}", _path);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private HistoryData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(_path));
                if (data != null)
                {
                    // Restore case-insensitive lookups lost in serialization.
                    data.Intros = new Dictionary<string, IntroStats>(data.Intros ?? new(), StringComparer.OrdinalIgnoreCase);
                    data.Users = new Dictionary<string, UserHistory>(data.Users ?? new(), StringComparer.OrdinalIgnoreCase);
                    data.Cursors ??= new();
                    data.FirstSeen = new Dictionary<string, DateTime>(data.FirstSeen ?? new(), StringComparer.OrdinalIgnoreCase);
                    data.DownloadedSlugs = new HashSet<string>(data.DownloadedSlugs ?? new(), StringComparer.OrdinalIgnoreCase);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intro history at {Path} is unreadable; starting fresh", _path);
        }

        return new HistoryData();
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private UserHistory User(string userId)
    {
        var id = Id(userId);
        if (!_data.Users.TryGetValue(id, out var u))
        {
            u = new UserHistory();
            _data.Users[id] = u;
        }

        u.RecentKeys ??= new();
        u.SeriesActivityUtc ??= new();
        u.SeriesIntroUtc ??= new();
        return u;
    }

    private IntroStats Stats(string key)
    {
        if (!_data.Intros.TryGetValue(key, out var s))
        {
            s = new IntroStats();
            _data.Intros[key] = s;
        }

        return s;
    }

    private static void Prune(UserHistory u, DateTime utcNow)
    {
        foreach (var k in u.SeriesActivityUtc.Where(kv => utcNow - kv.Value > KeepSeriesFor).Select(kv => kv.Key).ToList())
        {
            u.SeriesActivityUtc.Remove(k);
        }

        foreach (var k in u.SeriesIntroUtc.Where(kv => utcNow - kv.Value > KeepSeriesFor).Select(kv => kv.Key).ToList())
        {
            u.SeriesIntroUtc.Remove(k);
        }
    }

    private static string Id(string id) => ConditionMatcher.NormalizeId(id);
}
