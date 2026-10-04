using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Registers plugin services with the Jellyfin DI container by scanning the
/// plugin assembly for classes annotated with <see cref="PluginServiceAttribute"/>.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        var assembly = typeof(PluginServiceRegistrator).Assembly;
        foreach (var (impl, attr) in DiscoverAnnotatedServices(assembly))
        {
            var serviceType = attr.ServiceType ?? impl;
            serviceCollection.Add(new ServiceDescriptor(serviceType, impl, attr.Lifetime));
        }

        // Encrypts the source-server API key at rest (base helper. Degrades to plaintext + a logged
        // warning when no provider is available). Keys live in a fixed directory under the Jellyfin
        // data folder with a pinned application name, otherwise a host launch-context change (app
        // update, desktop-app vs service, Docker) shifts the Data Protection discriminator and every
        // stored secret fails to decrypt. See StableSecretProtection.
        serviceCollection.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.ServerSync.SecretProtector");
            return StableSecretProtection.CreateProtector(sp.GetService<MediaBrowser.Common.Configuration.IApplicationPaths>(), logger);
        });

        // The shared writer for Jellyfin's activity log, from the base package.
        serviceCollection.AddSingleton<ActivityLogger>();

        // The hint pipeline runs for the life of the server. Each worker is registered once as itself,
        // by the attribute scan above, and once more as a hosted service that resolves that same
        // instance, so the controllers and the publisher talk to the running worker.
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Queue.OutboundHintWorker>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Queue.InboundHintWorker>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Queue.LocalChangeObserver>());
        serviceCollection.AddHostedService<Tasks.HiddenTaskScheduleCleaner>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Queue.QueueMaintenanceService>());

        // Named HttpClient for source server communication. 
        // HandlerLifetime caps DNS staleness for the long-lived plugin process.
        // A connect timeout keeps one unreachable peer from holding a worker for the operating system's
        // own connect timeout, which runs to minutes.
        serviceCollection
            .AddHttpClient(SourceServerClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = Services.Queue.HintProtocol.ConnectTimeout
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));

        // The same client for entries that disallow private networks, with the rule enforced on every
        // address a name resolves to, so a name cannot stand in for an address the URL check refuses.
        serviceCollection
            .AddHttpClient(SourceServerClient.PublicHttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = Services.Queue.HintProtocol.ConnectTimeout,
                ConnectCallback = Utilities.ConfigurationUtilities.ConnectPublicOnlyAsync
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    }

    private static IEnumerable<(Type Impl, PluginServiceAttribute Attr)> DiscoverAnnotatedServices(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                continue;
            }

            var attr = type.GetCustomAttribute<PluginServiceAttribute>(inherit: false);
            if (attr == null)
            {
                continue;
            }

            yield return (type, attr);
        }
    }
}
