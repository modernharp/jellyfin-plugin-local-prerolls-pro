using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LocalPrerollsPro.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LocalPrerollsPro;

public class PrerollsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public PrerollsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static PrerollsPlugin Instance { get; private set; }

    public override string Name => "Local Pre-Rolls Pro";

    public override Guid Id => Guid.Parse("d18a3ff6-9bbb-4218-9158-c9d2642eee86");

    public override string Description => "Plays pre-roll videos from local folders and prerolls.me before movies and episodes, chosen by rules.";

    /// <summary>Cleans up settings as they are saved, so bad values can't reach the rest of the plugin.</summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            ConfigSanitizer.Sanitize(config);
        }

        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html",
        };
    }
}
