using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// A file's media segments as a Metadata row holds them: the segments, the source file they fit, and where they came from.
/// The source value is what the pool holds for the file, and the local value is what the local segment table holds.
/// </summary>
/// <param name="ServerKey">The key of the server entry the segments were read from.</param>
/// <param name="SourceItemId">The item's id on that server.</param>
/// <param name="OriginServerId">The Jellyfin id of the server the segments were first made on, or null.</param>
/// <param name="Size">The size of that server's file in bytes, or null.</param>
/// <param name="RunTimeTicks">How long that server's file runs, in ticks, or null.</param>
/// <param name="Segments">The segments, empty when no server has any for the file.</param>
public sealed record MediaSegmentsValue(
    string ServerKey,
    string SourceItemId,
    string? OriginServerId,
    long? Size,
    long? RunTimeTicks,
    IReadOnlyList<PeerSegment> Segments)
{
    /// <summary>Writes the value as the JSON a row stores.</summary>
    /// <returns>The JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Reads a value a row stores.</summary>
    /// <param name="json">The stored JSON, or null.</param>
    /// <returns>The value, or null when there is none or it cannot be read.</returns>
    public static MediaSegmentsValue? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MediaSegmentsValue>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the value of a row the local segment table holds.</summary>
    /// <param name="row">The row, or null.</param>
    /// <returns>The value, or null when there is no row.</returns>
    public static MediaSegmentsValue? FromRow(SourceMediaSegments? row)
        => row is null ? null : new(row.ServerKey, row.SourceItemId, row.OriginServerId, row.Size, row.RunTimeTicks, row.Segments.ToList());

    /// <summary>Builds the row the local segment table stores for a file.</summary>
    /// <param name="localItemId">The local item's id, without dashes.</param>
    /// <param name="at">When the row is written, in UTC.</param>
    /// <returns>The row.</returns>
    public SourceMediaSegments ToRow(string localItemId, DateTime at)
        => new(localItemId, ServerKey, SourceItemId, Segments, Size, RunTimeTicks, at, OriginServerId);

    /// <summary>
    /// Gives the pool's value for a row, or null when it would change nothing: no server has segments, none are kept here, and removing is off.
    /// </summary>
    /// <param name="chosen">What the pool holds for the file, or null.</param>
    /// <param name="local">What the local segment table holds for the file, or null.</param>
    /// <param name="remove">Whether Remove Segments No Server Has is on.</param>
    /// <returns>The value the row holds as its source.</returns>
    public static MediaSegmentsValue? Worth(MediaSegmentsValue? chosen, MediaSegmentsValue? local, bool remove)
        => chosen is { Segments.Count: 0 } && local is null && !remove ? null : chosen;

    /// <summary>
    /// Picks what the pool holds for one file from the answers of every server holding it, in rank order. The first server with segments wins.
    /// When none has any, the first that holds the file speaks, with no segments. A server's answer is passed over when the segments were first made on this server.
    /// </summary>
    /// <param name="answers">Each holding server's key, its Jellyfin id, the item's id there, and its answer, in rank order.</param>
    /// <param name="thisServerId">This server's Jellyfin id.</param>
    /// <returns>The value, or null when no server holds the file.</returns>
    public static MediaSegmentsValue? Choose(IEnumerable<(string ServerKey, string? ServerId, string SourceItemId, PeerSegmentsItem Answer)> answers, string thisServerId)
    {
        ArgumentNullException.ThrowIfNull(answers);
        MediaSegmentsValue? empty = null;
        foreach (var (serverKey, serverId, sourceItemId, answer) in answers)
        {
            if (answer is not { Found: true })
            {
                continue;
            }

            var origin = !string.IsNullOrWhiteSpace(answer.OriginServerId) ? answer.OriginServerId
                : !string.IsNullOrWhiteSpace(serverId) ? serverId
                : null;

            // Segments first made here came back through another server, so this server's own are kept.
            if (string.Equals(origin, thisServerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = new MediaSegmentsValue(serverKey, sourceItemId, origin, answer.Size, answer.RunTimeTicks, answer.Segments ?? new List<PeerSegment>());
            if (value.Segments.Count > 0)
            {
                return value;
            }

            empty ??= value;
        }

        return empty;
    }
}
