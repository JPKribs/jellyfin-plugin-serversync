using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// The media segments a source holds for the file of one local item, as the metadata scan last read them.
/// </summary>
/// <param name="LocalItemId">The local item's id, without dashes.</param>
/// <param name="ServerKey">The key of the server entry the segments came from.</param>
/// <param name="SourceItemId">The item's id on that server.</param>
/// <param name="Segments">The segments, empty when the source holds none.</param>
/// <param name="Size">The size of the source's file in bytes, or null.</param>
/// <param name="RunTimeTicks">How long the source's file runs, in ticks, or null.</param>
/// <param name="FetchedAt">When the scan read them, in UTC.</param>
/// <param name="OriginServerId">The Jellyfin id of the server the segments were first made on, or null when it is not known.</param>
public sealed record SourceMediaSegments(
    string LocalItemId,
    string ServerKey,
    string SourceItemId,
    IReadOnlyList<PeerSegment> Segments,
    long? Size,
    long? RunTimeTicks,
    DateTime FetchedAt,
    string? OriginServerId = null);
