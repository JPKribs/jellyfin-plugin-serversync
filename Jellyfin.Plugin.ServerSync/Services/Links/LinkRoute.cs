namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link streams from right now: a server entry here, and that server's id of the file.</summary>
/// <param name="ServerKey">The key of the server entry to stream from.</param>
/// <param name="ItemId">That server's id of the file.</param>
public sealed record LinkRoute(string ServerKey, string ItemId);
