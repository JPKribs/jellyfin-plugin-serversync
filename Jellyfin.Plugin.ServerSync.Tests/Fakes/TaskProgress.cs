using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Records each progress value a task reports, on the reporting thread and in order.
/// </summary>
public sealed class TaskProgress : IProgress<double>
{
    /// <summary>
    /// Gets the values reported so far.
    /// </summary>
    public List<double> Values { get; } = new();

    /// <inheritdoc/>
    public void Report(double value)
    {
        lock (Values)
        {
            Values.Add(value);
        }
    }
}
