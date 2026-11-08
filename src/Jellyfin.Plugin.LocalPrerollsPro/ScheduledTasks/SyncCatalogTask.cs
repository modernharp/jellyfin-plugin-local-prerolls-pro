using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LocalPrerollsPro.ScheduledTasks;

/// <summary>Downloads new prerolls for the prerolls.me subscriptions. Runs hourly (the site asks for no more).</summary>
public class SyncCatalogTask : IScheduledTask
{
    private readonly CatalogService _catalog;

    public SyncCatalogTask(CatalogService catalog)
    {
        _catalog = catalog;
    }

    public string Name => "Sync prerolls.me subscriptions";

    public string Key => "LocalPrerollsProCatalogSync";

    public string Description => "Downloads new prerolls from prerolls.me for the categories and tags you follow.";

    public string Category => "Local Pre-Rolls Pro";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (PrerollsPlugin.Instance.Configuration.CatalogAutoSync)
        {
            await _catalog.SyncAsync(false, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks };
    }
}
