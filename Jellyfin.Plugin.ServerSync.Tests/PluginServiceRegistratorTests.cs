using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tasks;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests;

/// <summary>
/// The registrator registers every marked service as it is marked, the background services, the secret protector, and both guarded HTTP clients.
/// </summary>
public sealed class PluginServiceRegistratorTests
{
    /// <summary>
    /// Every concrete class marked as a plugin service is registered under its service type with its lifetime.
    /// True: a class gains its registration by carrying the attribute alone.
    /// False: a marked service would be missing at start, and Jellyfin would fail to build what needs it.
    /// </summary>
    [Fact]
    public void RegisterServices_EveryMarkedClass_IsRegisteredAsMarked()
    {
        var services = Registered();
        var marked = typeof(PluginServiceRegistrator).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition)
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<PluginServiceAttribute>(inherit: false)))
            .Where(m => m.Attribute is not null)
            .ToList();

        var missing = marked
            .Where(m => !services.Any(d => d.ServiceType == (m.Attribute!.ServiceType ?? m.Type) && d.ImplementationType == m.Type && d.Lifetime == m.Attribute.Lifetime))
            .Select(m => m.Type.Name)
            .ToList();

        Assert.NotEmpty(marked);
        Assert.Empty(missing);
    }

    /// <summary>
    /// A marked class is registered exactly once.
    /// True: a singleton such as the database provider has one instance for the whole plugin.
    /// False: two registrations would give two stores over one database, each with its own state.
    /// </summary>
    [Fact]
    public void RegisterServices_MarkedClass_IsRegisteredOnce()
    {
        var services = Registered();

        var repeated = services
            .Where(d => d.ImplementationType?.GetCustomAttribute<PluginServiceAttribute>(inherit: false) is not null)
            .GroupBy(d => d.ImplementationType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!.Name)
            .ToList();

        Assert.Empty(repeated);
    }

    /// <summary>
    /// Nine background services are registered, and only the schedule cleaner is registered ungated.
    /// True: every worker that needs the database starts through the gate that skips it while Server Sync is held.
    /// False: a worker registered directly would stop Jellyfin from starting on an older database.
    /// </summary>
    [Fact]
    public void RegisterServices_BackgroundServices_AreGatedExceptTheScheduleCleaner()
    {
        var hosted = Registered().Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType!).ToList();

        var ungated = hosted.Where(t => !t.IsGenericType).ToList();
        var gated = hosted.Where(t => t.IsGenericType).Select(t => t.GetGenericArguments()[0].Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { typeof(HiddenTaskScheduleCleaner) }, ungated);
        Assert.Equal(
            new[] { "InboundHintWorker", "LinkCacheFiller", "LinkPlaybackReporter", "LinkProbe", "LinkSessionSweeper", "LocalChangeObserver", "OutboundHintWorker", "QueueMaintenanceService" },
            gated);
    }

    /// <summary>
    /// The secret protector is registered.
    /// True: every store that keeps a credential can be built.
    /// False: API keys and pairing secrets could not be protected, and the plugin would not start.
    /// </summary>
    [Fact]
    public void RegisterServices_SecretProtector_IsRegistered()
    {
        var services = Registered();

        Assert.Contains(services, d => d.ServiceType == typeof(SecretProtector));
    }

    /// <summary>
    /// The link playback filter is added to MVC as a service filter.
    /// True: Jellyfin's own playback routes run the filter that makes a link play.
    /// False: links would be handed to clients as loopback URLs they cannot open.
    /// </summary>
    [Fact]
    public void RegisterServices_MvcOptions_CarryTheLinkPlaybackFilter()
    {
        using var provider = Provider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;

        Assert.Contains(filters, f => f is ServiceFilterAttribute service && service.ServiceType == typeof(LinkPlaybackFilter));
    }

    /// <summary>
    /// Both named clients to other servers allow five minutes for a call.
    /// True: a large transfer is not cut off at the default hundred seconds.
    /// False: long downloads would fail partway on every run.
    /// </summary>
    [Theory]
    [InlineData(SourceServerClient.HttpClientName)]
    [InlineData(SourceServerClient.PublicHttpClientName)]
    public void RegisterServices_NamedClient_AllowsFiveMinutes(string name)
    {
        using var provider = Provider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }

    /// <summary>
    /// The two named clients are distinct.
    /// True: the client for public addresses only and the one that allows private networks are configured apart.
    /// False: one name would overwrite the other, and the address check would be the same for both.
    /// </summary>
    [Fact]
    public void RegisterServices_NamedClients_AreTwoDifferentNames()
    {
        Assert.NotEqual(SourceServerClient.HttpClientName, SourceServerClient.PublicHttpClientName);
        Assert.False(string.IsNullOrWhiteSpace(SourceServerClient.HttpClientName));
        Assert.False(string.IsNullOrWhiteSpace(SourceServerClient.PublicHttpClientName));
    }

    /// <summary>
    /// The registrator refuses a missing service collection.
    /// True: the mistake is named at once.
    /// False: registration would fail with an unrelated null reference.
    /// </summary>
    [Fact]
    public void RegisterServices_NullCollection_Throws()
    {
        var registrator = new PluginServiceRegistrator();

        Assert.Throws<ArgumentNullException>(() => registrator.RegisterServices(null!, null!));
    }

    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        return services;
    }

    private static ServiceProvider Provider() => Registered().BuildServiceProvider();
}
