using System;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Marks a class for automatic service registration by <see cref="PluginServiceRegistrator"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginServiceAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginServiceAttribute"/> class.
    /// </summary>
    /// <param name="lifetime">How long each registered instance lives.</param>
    public PluginServiceAttribute(ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
    }

    /// <summary>
    /// Gets how long each registered instance lives.
    /// </summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>
    /// Gets or sets the interface the class registers under, or null to register as itself.
    /// </summary>
    public Type? ServiceType { get; set; }
}
