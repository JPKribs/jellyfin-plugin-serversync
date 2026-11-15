using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A service provider that hands out the instances a test registered, answers a missing service with
/// null, and records every type it was asked for.
/// </summary>
public sealed class TaskServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    /// <summary>
    /// Gets or sets a value indicating whether a typed logger is supplied without being registered.
    /// </summary>
    public bool SuppliesLoggers { get; set; }

    /// <summary>
    /// Gets the types asked for, in order.
    /// </summary>
    public List<Type> Requested { get; } = new();

    /// <summary>
    /// Registers the instance returned for a service type.
    /// </summary>
    /// <typeparam name="T">The service type callers ask for.</typeparam>
    /// <param name="instance">The instance to return.</param>
    /// <returns>This provider, so registrations can be chained.</returns>
    public TaskServiceProvider With<T>(T instance)
        where T : class
    {
        _services[typeof(T)] = instance;
        return this;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType)
    {
        Requested.Add(serviceType);
        if (_services.TryGetValue(serviceType, out var service))
        {
            return service;
        }

        if (SuppliesLoggers && serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()));
        }

        return null;
    }
}
