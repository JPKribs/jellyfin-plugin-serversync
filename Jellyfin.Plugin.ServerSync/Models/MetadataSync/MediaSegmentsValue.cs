using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// A file's media segments as a Metadata row holds them: the segments, the source file they fit, and where they came from.
/// The source value is what the pool holds for the file, and the local value is what the local segment table holds.
/// </summary>
/// <param name="ServerKey">The key of the server entry the segments were read from.</param>
/// <param name="SourceItemId">The item's id on that server.</param>
/// <param name="OriginServerId">The Jellyfin id of the server the segments were first made on, or null when it is not known or they came from more than one. Each segment names its own.</param>
/// <param name="Size">The size of that server's file in bytes, or null.</param>
/// <param name="RunTimeTicks">How long that server's file runs, in ticks, or null.</param>
/// <param name="Segments">The segments, empty when no server has any for the file.</param>
/// <param name="HeldTypes">The segment types this server made itself and some server holds. This server's own segments of these types are kept, and the pool's are not used.</param>
public sealed record MediaSegmentsValue(
    string ServerKey,
    string SourceItemId,
    string? OriginServerId,
    long? Size,
    long? RunTimeTicks,
    IReadOnlyList<PeerSegment> Segments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? HeldTypes = null)
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
        => row is null ? null : new(row.ServerKey, row.SourceItemId, row.OriginServerId, row.Size, row.RunTimeTicks, row.Segments.ToList(), row.HeldTypes);

    /// <summary>Builds the row the local segment table stores for a file.</summary>
    /// <param name="localItemId">The local item's id, without dashes.</param>
    /// <param name="at">When the row is written, in UTC.</param>
    /// <returns>The row.</returns>
    public SourceMediaSegments ToRow(string localItemId, DateTime at)
        => new(localItemId, ServerKey, SourceItemId, Segments, Size, RunTimeTicks, at, OriginServerId, HeldTypes);

    /// <summary>
    /// Gives the value with its overlapping segments consolidated, as the Consolidate settings ask.
    /// </summary>
    /// <param name="intros">Whether overlapping intros shrink to the part they share.</param>
    /// <param name="outros">Whether overlapping outros shrink to the part they share.</param>
    /// <param name="others">Whether overlapping segments of any other type, of the same type, join into one that covers both.</param>
    /// <returns>The value, the same one when nothing is consolidated.</returns>
    public MediaSegmentsValue Consolidate(bool intros, bool outros, bool others)
        => !intros && !outros && !others ? this : this with { Segments = ConsolidateSegments(Segments, intros, outros, others) };

    /// <summary>
    /// Consolidates overlapping segments of the same type. Intros and outros shrink to the part every overlapping one shares,
    /// so 0:00 to 0:30 and 0:05 to 0:45 become 0:05 to 0:30. Any other type joins into one segment covering both, so 1:00 to 1:30
    /// and 1:05 to 1:45 become 1:00 to 1:45. Segments that do not overlap, or whose type is not consolidated, are kept as they are.
    /// A consolidated segment keeps the origin of the first one it was made from, so the server that made it never takes it back.
    /// </summary>
    /// <param name="segments">The segments.</param>
    /// <param name="intros">Whether overlapping intros shrink to the part they share.</param>
    /// <param name="outros">Whether overlapping outros shrink to the part they share.</param>
    /// <param name="others">Whether overlapping segments of any other type join into one.</param>
    /// <returns>The segments, ordered by where they start.</returns>
    public static IReadOnlyList<PeerSegment> ConsolidateSegments(IEnumerable<PeerSegment> segments, bool intros, bool outros, bool others)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var result = new List<PeerSegment>();
        foreach (var group in segments.Where(s => s is not null).GroupBy(s => s.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var isIntro = string.Equals(group.Key, "Intro", StringComparison.OrdinalIgnoreCase);
            var isOutro = string.Equals(group.Key, "Outro", StringComparison.OrdinalIgnoreCase);
            var shrink = (isIntro && intros) || (isOutro && outros);
            var join = !isIntro && !isOutro && others;
            if (!shrink && !join)
            {
                result.AddRange(group);
                continue;
            }

            // A segment with no length overlaps nothing, so it is kept as it is.
            result.AddRange(group.Where(s => s.EndTicks <= s.StartTicks));
            PeerSegment? current = null;
            foreach (var segment in group.Where(s => s.EndTicks > s.StartTicks).OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks))
            {
                if (current is not null && segment.StartTicks < current.EndTicks && segment.EndTicks > current.StartTicks)
                {
                    current = shrink
                        ? new PeerSegment { Type = current.Type, StartTicks = Math.Max(current.StartTicks, segment.StartTicks), EndTicks = Math.Min(current.EndTicks, segment.EndTicks), OriginServerId = current.OriginServerId ?? segment.OriginServerId }
                        : new PeerSegment { Type = current.Type, StartTicks = current.StartTicks, EndTicks = Math.Max(current.EndTicks, segment.EndTicks), OriginServerId = current.OriginServerId ?? segment.OriginServerId };
                    continue;
                }

                if (current is not null)
                {
                    result.Add(current);
                }

                current = new PeerSegment { Type = segment.Type, StartTicks = segment.StartTicks, EndTicks = segment.EndTicks, OriginServerId = segment.OriginServerId };
            }

            if (current is not null)
            {
                result.Add(current);
            }
        }

        return result.OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToList();
    }

    /// <summary>
    /// Gives the pool's value for a row, or null when it would change nothing: no server has segments, none are kept here, and removing is off.
    /// </summary>
    /// <param name="chosen">What the pool holds for the file, or null.</param>
    /// <param name="local">What the local segment table holds for the file, or null.</param>
    /// <param name="remove">Whether Remove segments no server has is on.</param>
    /// <returns>The value the row holds as its source.</returns>
    public static MediaSegmentsValue? Worth(MediaSegmentsValue? chosen, MediaSegmentsValue? local, bool remove)
        => chosen is { Segments.Count: 0 } && local is null && !remove ? null : chosen;

    /// <summary>
    /// Picks what the pool holds for one file from the answers of every server holding it, in rank order, type by type.
    /// For each type, the highest ranked server with segments of that type wins, among servers whose file matches the first one taken from.
    /// When none has any, the first that holds the file speaks, with no segments.
    /// A segment first made on this server is never taken back. It holds its type at the rank of the server that holds it: this server keeps
    /// its own segments of that type, and no server at that rank or below gives segments of that type. So a change made here flows out,
    /// and an older copy coming back around never undoes it.
    /// </summary>
    /// <param name="answers">Each holding server's key, its Jellyfin id, the item's id there, and its answer, in rank order.</param>
    /// <param name="thisServerId">This server's Jellyfin id.</param>
    /// <returns>The value, or null when no server holds the file, or every segment the servers hold is of a held type and none holds the file with no segments.</returns>
    public static MediaSegmentsValue? Choose(IEnumerable<(string ServerKey, string? ServerId, string SourceItemId, PeerSegmentsItem Answer)> answers, string thisServerId)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var held = new List<string>();
        var taken = new List<PeerSegment>();
        var takenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (string ServerKey, string SourceItemId, PeerSegmentsItem Answer)? lead = null;
        (string ServerKey, string? ServerId, string SourceItemId, PeerSegmentsItem Answer)? firstEmpty = null;
        foreach (var (serverKey, serverId, sourceItemId, answer) in answers)
        {
            if (answer is not { Found: true })
            {
                continue;
            }

            var segments = WithOrigins(answer, serverId);
            foreach (var type in segments.Where(s => string.Equals(s.OriginServerId, thisServerId, StringComparison.OrdinalIgnoreCase)).Select(s => s.Type ?? string.Empty))
            {
                if (!takenTypes.Contains(type) && !held.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    held.Add(type);
                }
            }

            if (segments.Count == 0)
            {
                firstEmpty ??= (serverKey, serverId, sourceItemId, answer);
                continue;
            }

            // Segments fit one file, so a server holding another version of it adds none.
            if (lead is { } first && !SameFile(first.Answer, answer))
            {
                continue;
            }

            var fresh = segments
                .Where(s => !held.Contains(s.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase) && !takenTypes.Contains(s.Type ?? string.Empty))
                .ToList();
            if (fresh.Count == 0)
            {
                continue;
            }

            lead ??= (serverKey, sourceItemId, answer);
            taken.AddRange(fresh);
            foreach (var segment in fresh)
            {
                takenTypes.Add(segment.Type ?? string.Empty);
            }
        }

        var heldTypes = held.Count > 0 ? held.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList() : null;
        if (lead is { } winner)
        {
            var origins = taken.Select(s => s.OriginServerId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ordered = taken.OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToList();
            return new MediaSegmentsValue(winner.ServerKey, winner.SourceItemId, origins.Count == 1 ? origins[0] : null, winner.Answer.Size, winner.Answer.RunTimeTicks, ordered, heldTypes);
        }

        if (firstEmpty is { } empty)
        {
            var origin = string.IsNullOrWhiteSpace(empty.ServerId) ? null : empty.ServerId;
            return new MediaSegmentsValue(empty.ServerKey, empty.SourceItemId, origin, empty.Answer.Size, empty.Answer.RunTimeTicks, new List<PeerSegment>(), heldTypes);
        }

        return null;
    }

    /// <summary>
    /// Tells whether two servers hold the same file, by size and by runtime to within a second, where an unknown size or runtime matches.
    /// </summary>
    private static bool SameFile(PeerSegmentsItem first, PeerSegmentsItem second)
        => (first.Size is not > 0 || second.Size is not > 0 || first.Size == second.Size)
            && (first.RunTimeTicks is not > 0 || second.RunTimeTicks is not > 0 || Math.Abs(first.RunTimeTicks.Value - second.RunTimeTicks.Value) <= TimeSpan.TicksPerSecond);

    /// <summary>
    /// Gives a server's segments, each naming the server it was first made on. A segment the server made itself names the server.
    /// An older release names one origin for all the segments it copied, and that origin is given to every segment in its answer.
    /// </summary>
    /// <param name="answer">The server's answer.</param>
    /// <param name="serverId">The server's Jellyfin id, or null when it is not known.</param>
    /// <returns>New segments, so the answer is left as it was.</returns>
    public static List<PeerSegment> WithOrigins(PeerSegmentsItem answer, string? serverId)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var self = string.IsNullOrWhiteSpace(serverId) ? null : serverId;
        var whole = string.IsNullOrWhiteSpace(answer.OriginServerId) ? self : answer.OriginServerId;
        return (answer.Segments ?? new List<PeerSegment>())
            .Where(s => s is not null)
            .Select(s => new PeerSegment
            {
                Type = s.Type,
                StartTicks = s.StartTicks,
                EndTicks = s.EndTicks,
                OriginServerId = answer.SegmentOrigins
                    ? (string.IsNullOrWhiteSpace(s.OriginServerId) ? self : s.OriginServerId)
                    : whole,
            })
            .ToList();
    }
}
