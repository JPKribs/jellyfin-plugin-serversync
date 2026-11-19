namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// A video or audio file the Metadata table matched on one source, whose media segments that source may hold.
/// </summary>
/// <param name="LocalItemId">The local item's id, without dashes.</param>
/// <param name="ServerKey">The key of the server entry that holds the same file.</param>
/// <param name="SourceItemId">The item's id on that server.</param>
public sealed record MediaSegmentCandidate(string LocalItemId, string ServerKey, string SourceItemId);
