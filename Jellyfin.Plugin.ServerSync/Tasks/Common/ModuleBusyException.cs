using System;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Thrown by a single row refresh or apply when the module's scheduled task holds the module, so the
/// caller can put the row off briefly instead of waiting out the whole scan.
/// </summary>
public sealed class ModuleBusyException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleBusyException"/> class.
    /// </summary>
    public ModuleBusyException()
        : base("the module is busy with a scheduled run")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleBusyException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ModuleBusyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleBusyException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ModuleBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
