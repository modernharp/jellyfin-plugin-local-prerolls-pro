using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalPrerollsPro.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalPrerollsPro;

/// <summary>Called by Jellyfin when a client asks which intros to play before an item.</summary>
public class IntroProvider : IIntroProvider
{
    private readonly PreShowBuilder _builder;
    private readonly ILogger<IntroProvider> _logger;

    public IntroProvider(PreShowBuilder builder, ILogger<IntroProvider> logger)
    {
        _builder = builder;
        _logger = logger;
    }

    public string Name => "Local Pre-Rolls Pro";

    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        try
        {
            var plan = _builder.Build(item, user, DateTime.Now, record: true);
            if (plan.Intros.Count == 0)
            {
                _logger.LogDebug("No intros for {Item}: {Reason}", item.Name, plan.SkipReason);
            }

            return Task.FromResult<IEnumerable<IntroInfo>>(
                plan.Intros.Select(p => new IntroInfo { Path = p.Path, ItemId = p.ItemId }).ToList());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error choosing intros for {Item}", item?.Name);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }
    }
}
