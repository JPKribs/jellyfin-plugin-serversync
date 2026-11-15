namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// One of this server's links: where the .strm sits, where the copy would have gone, the id in its URL,
/// the server entry it streams from, and the file's size there, or 0 when unknown.
/// </summary>
public sealed record LinkEntry(string LinkPath, string CopiedPath, string LinkId, string? ServerKey, long Size = 0);
