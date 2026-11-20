using System.Collections.Generic;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// What a file's media segments become on a Media Segment Scan.
/// </summary>
/// <param name="Ours">The segments Server Sync gives the file.</param>
/// <param name="Remove">The segments other providers gave the file that are removed, each replaced by the pool's of the same type.</param>
public sealed record SegmentPlan(IReadOnlyList<MediaSegmentDto> Ours, IReadOnlyList<MediaSegmentDto> Remove);
