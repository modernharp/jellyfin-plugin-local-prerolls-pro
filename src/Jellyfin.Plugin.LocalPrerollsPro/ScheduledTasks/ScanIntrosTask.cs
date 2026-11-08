using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LocalPrerollsPro.ScheduledTasks;

/// <summary>Picks up intro files that were added, removed or renamed. Shown under Dashboard > Scheduled Tasks.</summary>
public class ScanIntrosTask : IScheduledTask
{
    private readonly IntroCatalog _catalog;

    public ScanIntrosTask(IntroCatalog catalog)
    {
        _catalog = catalog;
    }

    public string Name => "Scan pre-roll folders";

    public string Key => "LocalPrerollsProScan";

    public string Description => "Finds new, removed and renamed pre-roll videos in the Local Pre-Rolls Pro folders.";

    public string Category => "Local Pre-Rolls Pro";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _catalog.ScanAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }
}
