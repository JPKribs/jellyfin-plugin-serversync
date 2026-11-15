using System;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services;

/// <summary>
/// The attribute must carry the lifetime and service type the registrator reads, and may mark a class only once.
/// </summary>
public sealed class PluginServiceAttributeTests
{
    /// <summary>
    /// The lifetime given is the lifetime read back.
    /// True: a singleton store is registered once and a transient client per use.
    /// False: a store would be built per request and lose its state.
    /// </summary>
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public void Constructor_Lifetime_IsKept(ServiceLifetime lifetime)
    {
        var attribute = new PluginServiceAttribute(lifetime);

        Assert.Equal(lifetime, attribute.Lifetime);
    }

    /// <summary>
    /// The service type is null until set, which registers the class as itself.
    /// True: a class with no interface resolves by its own type.
    /// False: it would be registered under a type nobody asks for.
    /// </summary>
    [Fact]
    public void ServiceType_UnsetThenSet_IsNullThenTheInterface()
    {
        var plain = new PluginServiceAttribute(ServiceLifetime.Singleton);
        var typed = new PluginServiceAttribute(ServiceLifetime.Singleton) { ServiceType = typeof(IDisposable) };

        Assert.Null(plain.ServiceType);
        Assert.Equal(typeof(IDisposable), typed.ServiceType);
    }

    /// <summary>
    /// The attribute fits classes only, once each, and is not inherited.
    /// True: a subclass is registered only when it is marked itself.
    /// False: a test double deriving from a service would be registered into Jellyfin by inheritance.
    /// </summary>
    [Fact]
    public void Usage_IsOneUninheritedMarkPerClass()
    {
        var usage = typeof(PluginServiceAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.Inherited);
        Assert.False(usage.AllowMultiple);
    }
}
