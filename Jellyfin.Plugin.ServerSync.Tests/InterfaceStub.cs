using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jellyfin.Plugin.ServerSync.Tests;

/// <summary>
/// A stand-in for a large Jellyfin interface where a test needs only a member or two. Every call is
/// recorded, members with an answer return it, and the rest return their type's default.
/// </summary>
public class InterfaceStub : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _answers = new(StringComparer.Ordinal);

    public List<(string Name, object?[] Args)> Calls { get; } = new();

    public static (T Proxy, InterfaceStub Stub) Create<T>()
        where T : class
    {
        var proxy = Create<T, InterfaceStub>();
        return (proxy, (InterfaceStub)(object)proxy);
    }

    public InterfaceStub Answer(string memberName, Func<object?[], object?> answer)
    {
        _answers[memberName] = answer;
        return this;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= Array.Empty<object?>();
        Calls.Add((targetMethod.Name, args));
        if (_answers.TryGetValue(targetMethod.Name, out var answer))
        {
            return answer(args);
        }

        var type = targetMethod.ReturnType;
        return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    }
}
