using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Registers every class marked with <see cref="PluginServiceAttribute"/>, the gated background services, and the secret protector.
/// It also registers the two HTTP clients for other servers, each with a connect timeout, the address check, the redirect refusal, retries, and one long try for a slow answer.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>What a refused redirect tells the administrator to do.</summary>
    private const string RedirectAdvice = "Set the server URL to the address it redirects to.";

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

        serviceCollection.AddPluginSecretProtector(Plugin.SecretsName);

        serviceCollection.AddActivityLogger();

        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.AddService<Services.Links.LinkPlaybackFilter>());
        serviceCollection.AddHostedService<Gate<Services.Links.LinkProbe>>();
        serviceCollection.AddHostedService<Gate<Services.Links.LinkPlaybackReporter>>();
        serviceCollection.AddHostedService<Gate<Services.Links.LinkSessionSweeper>>();
        serviceCollection.AddHostedService<Gate<Services.Links.LinkCacheFiller>>();

        serviceCollection.AddHostedService<Gate<Services.Queue.OutboundHintWorker>>();
        serviceCollection.AddHostedService<Gate<Services.Queue.InboundHintWorker>>();
        serviceCollection.AddHostedService<Gate<Services.Queue.LocalChangeObserver>>();
        serviceCollection.AddHostedService<Tasks.HiddenTaskScheduleCleaner>();
        serviceCollection.AddHostedService<Gate<Services.Queue.QueueMaintenanceService>>();

        serviceCollection
            .AddHttpClient(SourceServerClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = Services.Queue.HintProtocol.ConnectTimeout,
                AllowAutoRedirect = false,
                ConnectCallback = Utilities.ConfigurationUtilities.ConnectPrivateAllowedAsync
            })
            .AddHttpMessageHandler(() => new SlowAnswerHandler())
            .AddHttpMessageHandler(() => new JPKribs.Jellyfin.Base.JpkRetryHandler())
            .AddHttpMessageHandler(() => new RedirectRefusingHandler(RedirectAdvice))
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));

        serviceCollection
            .AddHttpClient(SourceServerClient.PublicHttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = Services.Queue.HintProtocol.ConnectTimeout,
                AllowAutoRedirect = false,
                ConnectCallback = Utilities.ConfigurationUtilities.ConnectPublicOnlyAsync
            })
            .AddHttpMessageHandler(() => new SlowAnswerHandler())
            .AddHttpMessageHandler(() => new JPKribs.Jellyfin.Base.JpkRetryHandler())
            .AddHttpMessageHandler(() => new RedirectRefusingHandler(RedirectAdvice))
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    }

    /// <summary>Finds every concrete class in the assembly that carries the service attribute.</summary>
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

    /// <summary>
    /// Starts one of the plugin's background services unless Server Sync is held, since a hosted service that cannot be built stops Jellyfin itself.
    /// The wrapped service is the one instance the controllers and the publisher use.
    /// </summary>
    /// <typeparam name="T">The wrapped hosted service.</typeparam>
    private sealed class Gate<T> : Microsoft.Extensions.Hosting.IHostedService
        where T : class, Microsoft.Extensions.Hosting.IHostedService
    {
        private readonly IServiceProvider _services;
        private T? _inner;

        /// <summary>Initializes a new instance of the <see cref="Gate{T}"/> class.</summary>
        /// <param name="services">Resolves the wrapped service.</param>
        public Gate(IServiceProvider services) => _services = services;

        /// <inheritdoc/>
        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken)
        {
            if (Plugin.Held)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            _inner = _services.GetRequiredService<T>();
            return _inner.StartAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken)
            => _inner?.StopAsync(cancellationToken) ?? System.Threading.Tasks.Task.CompletedTask;
    }
}
