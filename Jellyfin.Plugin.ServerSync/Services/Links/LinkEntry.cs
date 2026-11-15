namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// One of this server's links: where the .strm sits, where the copy would have gone, the id in its URL,
/// the server entry it streams from, and the file's size there, or 0 when unknown.
/// </summary>
/// <param name="LinkPath">Where the .strm sits.</param>
/// <param name="CopiedPath">Where a full copy would have gone, with the source file's extension.</param>
/// <param name="LinkId">The id in the link's URL, which is the source's id of the file.</param>
/// <param name="ServerKey">The key of the server entry the link streams from.</param>
/// <param name="Size">The file's size on the source in bytes, or 0 when unknown.</param>
public sealed record LinkEntry(string LinkPath, string CopiedPath, string LinkId, string? ServerKey, long Size = 0);
