using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// One scan server for the duration of a run: the configured entry, a connected client for it, and its
/// priority, which is its position in the configured list. Lower is better.
/// </summary>
public sealed class ScanSource : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScanSource"/> class.
    /// </summary>
    /// <param name="server">The configured entry.</param>
    /// <param name="client">A client bound to that entry.</param>
    /// <param name="priority">The entry's position in the configured list.</param>
    public ScanSource(SourceServer server, SourceServerClient client, int priority)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Priority = priority;
    }

    /// <summary>Gets the configured entry.</summary>
    public SourceServer Server { get; }

    /// <summary>Gets the client bound to the entry for this run.</summary>
    public SourceServerClient Client { get; }

    /// <summary>Gets the entry's priority. Lower wins.</summary>
    public int Priority { get; }

    /// <summary>Gets the entry key, carried onto every row built from this source.</summary>
    public string Key => Server.Key;

    /// <summary>Gets the name used in logs.</summary>
    public string Name => Server.DisplayName;

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();
}
