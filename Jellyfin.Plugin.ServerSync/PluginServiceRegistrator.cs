using System;
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
        serviceCollection.AddTransient<ContentSyncTableManager>();
        serviceCollection.AddTransient<DownloadService>();
        serviceCollection.AddTransient<MetadataSyncTableService>();
        serviceCollection.AddTransient<MetadataSyncTableManager>();
        serviceCollection.AddTransient<HistorySyncTableService>();
        serviceCollection.AddTransient<HistorySyncTableManager>();
        serviceCollection.AddTransient<LocalServerClient>();
        serviceCollection.AddTransient<UserSyncTableManager>();
        serviceCollection.AddTransient<UserSyncTableService>();
        serviceCollection.AddTransient<PeopleSyncTableService>();
        serviceCollection.AddTransient<PeopleSyncTableManager>();

        // Named HttpClient for source server communication.
        //
        // Timeout governs the time to receive response *headers* on requests
        // that use HttpCompletionOption.ResponseHeadersRead (downloads). It
        // does NOT bound stream-read time once headers are in. A bounded
        // timeout here protects against hung Kiota API calls (which buffer
        // the full response) while allowing long downloads.
        //
        // HandlerLifetime caps DNS staleness for long-lived plugin lifetimes.
        serviceCollection
            .AddHttpClient(SourceServerClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    }
}
