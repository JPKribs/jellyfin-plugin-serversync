using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for the service container. It hands out the instances a test registers, answers null for the rest, and records the types asked for.
/// </summary>
public sealed class QueueServices : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    /// <summary>Gets the types that were asked for, in order.</summary>
    public List<Type> Asked { get; } = new();

    /// <summary>Registers an instance under its type.</summary>
    /// <typeparam name="T">The type the instance is asked for by.</typeparam>
    /// <param name="service">The instance to hand out.</param>
    /// <returns>This container.</returns>
    public QueueServices Add<T>(T service)
        where T : class
    {
        _services[typeof(T)] = service;
        return this;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType)
    {
        Asked.Add(serviceType);
        return _services.TryGetValue(serviceType, out var service) ? service : null;
    }
}
