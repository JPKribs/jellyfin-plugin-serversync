using System.Collections.Generic;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// What a file's media segments become on a Media Segment Scan.
/// </summary>
/// <param name="Ours">The segments Server Sync gives the file.</param>
/// <param name="RemoveOthers">Whether the segments other providers gave the file are removed.</param>
public sealed record SegmentPlan(IReadOnlyList<MediaSegmentDto> Ours, bool RemoveOthers);
