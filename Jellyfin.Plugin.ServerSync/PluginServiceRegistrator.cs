using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.DataProtection;
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

        // Encrypts the source-server API key at rest (base helper; degrades to plaintext + a logged
        // warning when no provider is available). Keys live in a fixed directory under the Jellyfin
        // data folder with a pinned application name — otherwise a host launch-context change (app
        // update, desktop-app vs service, Docker) shifts the Data Protection discriminator and every
        // stored secret fails to decrypt. See StableSecretProtection.
        serviceCollection.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.ServerSync.SecretProtector");
            IDataProtectionProvider? provider = null;

            var paths = sp.GetService<MediaBrowser.Common.Configuration.IApplicationPaths>();
            if (paths is not null)
            {
                var keyDirectory = System.IO.Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.ServerSync.Keys");
                provider = StableSecretProtection.Build(keyDirectory, logger);
            }

            return new SecretProtector("Jellyfin.Plugin.ServerSync.Secrets.v1", logger, provider);
        });

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
