using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Core abstractions (singletons)
        serviceCollection.AddSingleton<ISyncDatabaseProvider, SyncDatabaseProvider>();
        serviceCollection.AddSingleton<IPluginConfigurationManager, PluginConfigurationManager>();
        serviceCollection.AddSingleton<ISourceServerClientFactory, SourceServerClientFactory>();

        // Services (transient — stateless, created per use)
        serviceCollection.AddTransient<DownloadService>();
        serviceCollection.AddTransient<SyncTableService>();
        serviceCollection.AddTransient<MetadataSyncTableService>();
        serviceCollection.AddTransient<HistorySyncTableService>();
        serviceCollection.AddTransient<LocalServerClient>();
        serviceCollection.AddTransient<UserSyncTableService>();
        serviceCollection.AddTransient<UserSyncStateService>();
        serviceCollection.AddTransient<PeopleSyncTableService>();

        // Named HttpClient for source server communication
        serviceCollection.AddHttpClient(SourceServerClient.HttpClientName);
    }
}
