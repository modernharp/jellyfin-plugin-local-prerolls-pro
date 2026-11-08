using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro.Services;

/// <summary>
/// Background work: upgrades old settings, scans at startup and after settings change,
/// and listens to playback to count plays and detect binges.
/// </summary>
public sealed class PrerollsService : IHostedService, IDisposable
{
    private readonly IntroCatalog _catalog;
    private readonly PlaybackHistory _history;
    private readonly IntroPreparer _preparer;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PrerollsService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Timer _rescanTimer;

    public PrerollsService(IntroCatalog catalog, PlaybackHistory history, IntroPreparer preparer, ILibraryManager libraryManager, ISessionManager sessionManager, ILogger<PrerollsService> logger)
    {
        _catalog = catalog;
        _history = history;
        _preparer = preparer;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rescanTimer = new Timer(_ => _ = RescanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        PrerollsPlugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        _preparer.Start();
        _ = Task.Run(StartupAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        PrerollsPlugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        _rescanTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _history.Flush();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _rescanTimer?.Dispose();
        _stopping.Dispose();
    }

    private async Task StartupAsync()
    {
        // The library may still be starting; retry for a while.
        for (var attempt = 1; attempt <= 10 && !_stopping.IsCancellationRequested; attempt++)
        {
            try
            {
                var plugin = PrerollsPlugin.Instance;
                var migrated = ConfigMigrator.Migrate(plugin.Configuration, id => _libraryManager.GetItemById(id)?.Path);
                var fixes = ConfigSanitizer.Sanitize(plugin.Configuration);
                foreach (var fix in fixes)
                {
                    _logger.LogWarning("Settings fixed: {Fix}", fix);
                }

                if (migrated || fixes.Count > 0)
                {
                    _logger.LogInformation("Set up Local Pre-Rolls Pro settings to version {Version}", PluginConfiguration.CurrentSchemaVersion);
                    plugin.SaveConfiguration();
                }

                await _catalog.ScanAsync(_stopping.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local Pre-Rolls Pro startup attempt {Attempt} failed", attempt);
                await Task.Delay(TimeSpan.FromSeconds(15), _stopping.Token).ConfigureAwait(false);
            }
        }
    }

    private void OnConfigurationChanged(object sender, BasePluginConfiguration e) =>
        _rescanTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); // Debounce bursts of saves.

    private async Task RescanAsync()
    {
        try
        {
            await _catalog.ScanAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Intro scan after settings change failed");
        }
    }

    private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (IntroCatalog.IsIntroItem(e.Item))
            {
                var key = _catalog.FindByPath(e.Item.Path)?.Key ?? _preparer.KeyForPreparedPath(e.Item.Path);
                if (key != null)
                {
                    _history.RecordPlay(key, DateTime.UtcNow);
                }

                return;
            }

            RecordEpisode(e);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record playback start");
        }
    }

    private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
    {
        try
        {
            RecordEpisode(e);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record playback stop");
        }
    }

    private void RecordEpisode(PlaybackProgressEventArgs e)
    {
        if (e.Item is not Episode episode || e.Users == null)
        {
            return;
        }

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            _history.RecordSeriesActivity(user.Id.ToString("N"), seriesId.ToString("N"), DateTime.UtcNow);
        }
    }
}
