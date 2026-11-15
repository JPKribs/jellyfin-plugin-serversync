#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Records the media files that left this server's library and when, for <see cref="Kept"/>.
/// Jellyfin keeps no record of removals, so without this a server reading this one would have to list everything.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class RemovedItemsStore : QueueStoreBase
{
    /// <summary>How long a removal is remembered. A server that asks from further back is told to list everything.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="RemovedItemsStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the sync database on first use.</param>
    /// <param name="logger">Writes log entries.</param>
    public RemovedItemsStore(ISyncDatabaseProvider databaseProvider, ILogger<RemovedItemsStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>Records that an item left the library.</summary>
    /// <param name="itemId">The item's id here, without dashes.</param>
    /// <param name="when">When it left.</param>
    /// <exception cref="ArgumentException">The id is empty.</exception>
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
    /// Reads the ids removed since a time, and the time the record reaches back to.
    /// A caller asking from before that time has missed removals this cannot name.
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
    /// Reads the copies removed since a time that another server gave this one, each with its origin.
    /// Jellyfin lists a removed file until its next library scan, so a peer still learns whose file it was.
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
