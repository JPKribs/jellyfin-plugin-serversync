#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.MetadataSync;

/// <summary>
/// Keeps the media segments each source holds for the files here, in the sync database, so the media segment provider answers without asking a source.
/// The metadata scan fills it from the files the Metadata table matched.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class MediaSegmentStore : QueueStoreBase
{
    /// <summary>The item types whose files can hold media segments.</summary>
    private static readonly string[] SegmentTypes = { "Movie", "Episode", "Video", "MusicVideo", "Trailer", "Audio", "AudioBook" };

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the sync database on first use.</param>
    /// <param name="logger">Writes log entries.</param>
    public MediaSegmentStore(ISyncDatabaseProvider databaseProvider, ILogger<MediaSegmentStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Lists the video and audio files the Metadata table matched on a source, one entry for each source that holds each file.
    /// </summary>
    /// <returns>The files, with the source that holds each.</returns>
    public IReadOnlyList<MediaSegmentCandidate> Candidates() => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT LocalItemId, ServerKey, SourceItemId FROM MetadataSyncItems
            WHERE IsFolder = 0 AND LocalItemId IS NOT NULL AND LocalItemId <> '' AND ServerKey IS NOT NULL AND ServerKey <> ''
                AND ItemType IN ({string.Join(", ", SegmentTypes.Select((_, i) => "@t" + i))})";
        for (var i = 0; i < SegmentTypes.Length; i++)
        {
            Add(cmd, "@t" + i, SegmentTypes[i]);
        }

        var candidates = new List<MediaSegmentCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(new MediaSegmentCandidate(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return (IReadOnlyList<MediaSegmentCandidate>)candidates;
    });

    /// <summary>
    /// Reads the segments kept for a local item.
    /// </summary>
    /// <param name="localItemId">The local item's id, without dashes.</param>
    /// <returns>The segments and the source file they fit, or null when none are kept.</returns>
    public SourceMediaSegments? Get(string localItemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(localItemId);
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LocalItemId, ServerKey, SourceItemId, Segments, Size, RunTimeTicks, FetchedAt, OriginServerId FROM SourceMediaSegments WHERE LocalItemId = @id";
            Add(cmd, "@id", localItemId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SourceMediaSegments(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                JsonSerializer.Deserialize<List<PeerSegment>>(reader.GetString(3)) ?? new List<PeerSegment>(),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Unstamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        });
    }

    /// <summary>
    /// Keeps the segments for each local item, replacing what was kept before, in one write.
    /// </summary>
    /// <param name="rows">The segments to keep.</param>
    public void Save(IReadOnlyCollection<SourceMediaSegments> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO SourceMediaSegments (LocalItemId, ServerKey, SourceItemId, Segments, Size, RunTimeTicks, FetchedAt, OriginServerId)
                VALUES (@local, @server, @source, @segments, @size, @runtime, @at, @origin)
                ON CONFLICT(LocalItemId) DO UPDATE SET ServerKey = excluded.ServerKey, SourceItemId = excluded.SourceItemId,
                    Segments = excluded.Segments, Size = excluded.Size, RunTimeTicks = excluded.RunTimeTicks, FetchedAt = excluded.FetchedAt,
                    OriginServerId = excluded.OriginServerId";
            foreach (var row in rows)
            {
                cmd.Parameters.Clear();
                Add(cmd, "@local", row.LocalItemId);
                Add(cmd, "@server", row.ServerKey);
                Add(cmd, "@source", row.SourceItemId);
                Add(cmd, "@segments", JsonSerializer.Serialize(row.Segments));
                Add(cmd, "@size", row.Size);
                Add(cmd, "@runtime", row.RunTimeTicks);
                Add(cmd, "@at", Stamp(row.FetchedAt));
                Add(cmd, "@origin", row.OriginServerId);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>
    /// Forgets the segments kept for local items whose source no longer holds the file.
    /// </summary>
    /// <param name="localItemIds">The local items' ids, without dashes.</param>
    /// <returns>How many were forgotten.</returns>
    public int Remove(IReadOnlyCollection<string> localItemIds)
    {
        ArgumentNullException.ThrowIfNull(localItemIds);
        if (localItemIds.Count == 0)
        {
            return 0;
        }

        return Write(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM SourceMediaSegments WHERE LocalItemId = @id";
            var removed = 0;
            foreach (var id in localItemIds)
            {
                cmd.Parameters.Clear();
                Add(cmd, "@id", id);
                removed += cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed;
        });
    }

    /// <summary>
    /// Forgets the segments kept for local items the Metadata table no longer matches, such as a file removed here or from every source.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public int RemoveUntracked() => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM SourceMediaSegments WHERE LocalItemId NOT IN (SELECT LocalItemId FROM MetadataSyncItems WHERE LocalItemId IS NOT NULL)";
        return cmd.ExecuteNonQuery();
    });
}
