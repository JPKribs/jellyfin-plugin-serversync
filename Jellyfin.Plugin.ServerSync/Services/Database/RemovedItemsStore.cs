#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// The media files that left this server's library, each with when. Jellyfin answers what was added
/// or changed since a given time, and nothing remembers what was removed, so a server reading this
/// one could only learn of a removal by listing everything. This is that one missing record. It holds
/// only what is gone, and only for <see cref="Kept"/>, so it never grows into a copy of the library.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class RemovedItemsStore : QueueStoreBase
{
    /// <summary>How long a removal is remembered. A server that asks from further back is told to list everything.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="RemovedItemsStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Database provider.</param>
    /// <param name="logger">Logger.</param>
    public RemovedItemsStore(ISyncDatabaseProvider databaseProvider, ILogger<RemovedItemsStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>Records that an item left the library.</summary>
    /// <param name="itemId">The item's id here, without dashes.</param>
    /// <param name="when">When it left.</param>
    public void Note(string itemId, DateTime when)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO RemovedItems (ItemId, RemovedAt) VALUES (@id, @at)";
            Add(cmd, "@id", itemId);
            Add(cmd, "@at", Stamp(when));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// The ids removed since a time, and the time the record reaches back to. A caller asking from
    /// before that time has missed removals this cannot name.
    /// </summary>
    /// <param name="since">Removals at or after this are returned.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The time the record covers from, and the ids.</returns>
    public (DateTime CoveredFrom, IReadOnlyList<string> ItemIds) Since(DateTime since, DateTime now) => Read(conn =>
    {
        var started = now;
        using (var start = conn.CreateCommand())
        {
            start.CommandText = "SELECT StartedAt FROM RemovedItemsLog LIMIT 1";
            if (start.ExecuteScalar() is string text)
            {
                started = Unstamp(text);
            }
        }

        var covered = started > now - Kept ? started : now - Kept;
        var ids = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT ItemId FROM RemovedItems WHERE RemovedAt >= @since AND ItemId <> ''";
        Add(cmd, "@since", Stamp(since));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return (covered, (IReadOnlyList<string>)ids);
    });

    /// <summary>
    /// The copies this server removed since a time that it had been given by another server, each
    /// with the server it first came from. Jellyfin goes on listing a removed file until it next
    /// scans the folder, so a peer reading the library in between is still told whose file it was.
    /// </summary>
    /// <param name="since">Removals at or after this are returned.</param>
    /// <returns>Path and origin pairs.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> CopiesSince(DateTime since) => Read(conn =>
    {
        var copies = new List<KeyValuePair<string, string>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Path, OriginServerId FROM RemovedItems WHERE RemovedAt >= @since AND Path IS NOT NULL AND OriginServerId IS NOT NULL AND OriginServerId <> ''";
        Add(cmd, "@since", Stamp(since));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            copies.Add(new KeyValuePair<string, string>(reader.GetString(0), reader.GetString(1)));
        }

        return (IReadOnlyList<KeyValuePair<string, string>>)copies;
    });

    /// <summary>Forgets removals older than a time.</summary>
    /// <param name="before">The cutoff.</param>
    /// <returns>How many were forgotten.</returns>
    public int Trim(DateTime before) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM RemovedItems WHERE RemovedAt < @before";
        Add(cmd, "@before", Stamp(before));
        return cmd.ExecuteNonQuery();
    });
}
