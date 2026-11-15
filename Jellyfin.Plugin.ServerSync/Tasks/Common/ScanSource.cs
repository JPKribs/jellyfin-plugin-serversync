using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// One source for the length of a run: its server entry, a connected client, and its rank.
/// </summary>
public sealed class ScanSource : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScanSource"/> class.
    /// </summary>
    /// <param name="server">The server entry the run reads from.</param>
    /// <param name="client">A client bound to that entry, which this instance disposes.</param>
    /// <param name="priority">The entry's rank, which is its place in the configured list.</param>
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

    /// <summary>Gets the entry's rank, where a lower number outranks a higher one.</summary>
    public int Priority { get; }

    /// <summary>Gets the entry key, carried onto every row built from this source.</summary>
    public string Key => Server.Key;

    /// <summary>Gets the name used in logs.</summary>
    public string Name => Server.DisplayName;

    /// <inheritdoc />
    public void Dispose() => Client.Dispose();
}
