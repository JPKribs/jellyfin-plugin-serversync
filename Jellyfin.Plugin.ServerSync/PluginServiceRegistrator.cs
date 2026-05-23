using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

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

        // Named HttpClient for source server communication. 
        // HandlerLifetime caps DNS staleness for the long-lived plugin process.
        serviceCollection
            .AddHttpClient(SourceServerClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(5);
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
