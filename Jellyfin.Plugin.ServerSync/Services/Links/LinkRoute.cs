namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link streams from right now: a server entry here, and that server's id of the file.</summary>
public sealed record LinkRoute(string ServerKey, string ItemId);
