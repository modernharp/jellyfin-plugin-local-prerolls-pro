using Jellyfin.Plugin.LocalPrerollsPro.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LocalPrerollsPro;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IntroCatalog>();
        serviceCollection.AddSingleton<PlaybackHistory>();
        serviceCollection.AddSingleton<IntroPreparer>();
        serviceCollection.AddSingleton<FeatureContextBuilder>();
        serviceCollection.AddSingleton<PreShowBuilder>();
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<LocalIntrosImporter>();
        serviceCollection.AddHostedService<PrerollsService>();
    }
}
