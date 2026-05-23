using System;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Marks a class for automatic DI registration by <see cref="PluginServiceRegistrator"/>.
/// Leave <see cref="ServiceType"/> null for concrete-only registration, or set it to
/// the interface for interface→implementation pairs.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginServiceAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="lifetime">DI lifetime for this service.</param>
    public PluginServiceAttribute(ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
    }

    /// <summary>
    /// DI lifetime (Singleton / Scoped / Transient).
    /// </summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>
    /// Optional service-type to register the implementation under. When null
    /// (the default) the implementation registers as itself. Set this to the
    /// interface for interface→implementation pairs.
    /// </summary>
    public Type? ServiceType { get; set; }
}
