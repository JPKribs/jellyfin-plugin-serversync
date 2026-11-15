using System;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Thrown by a single row refresh or apply when the sync type's scheduled task holds it, so the caller can put the row off instead of waiting out the scan.
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
    /// <param name="message">Why the sync type is busy.</param>
    public ModuleBusyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModuleBusyException"/> class.
    /// </summary>
    /// <param name="message">Why the sync type is busy.</param>
    /// <param name="innerException">The exception that led to this one.</param>
    public ModuleBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
