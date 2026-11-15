using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Jellyfin.Plugin.ServerSync.Tasks.Common;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Calls the static members of the internal task helpers, which the test project cannot name, and
/// rethrows what they throw as the caller would see it.
/// </summary>
public static class TaskInternals
{
    /// <summary>
    /// Calls a static method of an internal type in the Tasks.Common namespace.
    /// </summary>
    /// <param name="typeName">The type's name, without its namespace.</param>
    /// <param name="method">The method's name.</param>
    /// <param name="args">The arguments, in order.</param>
    /// <returns>What the method returned, or null for none.</returns>
    public static object? Call(string typeName, string method, params object?[] args)
    {
        var target = Find(typeName).GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(typeName, method);
        try
        {
            return target.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Reads a public static field of an internal type in the Tasks.Common namespace.
    /// </summary>
    /// <param name="typeName">The type's name, without its namespace.</param>
    /// <param name="field">The field's name.</param>
    /// <returns>The field's value.</returns>
    public static object? Field(string typeName, string field)
        => (Find(typeName).GetField(field, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(typeName, field)).GetValue(null);

    private static Type Find(string typeName)
        => typeof(ScanSource).Assembly.GetType("Jellyfin.Plugin.ServerSync.Tasks.Common." + typeName, throwOnError: true)!;
}
