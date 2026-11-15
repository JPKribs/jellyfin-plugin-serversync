namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>
/// The record of one local playlist that is a copy of another server's.
/// </summary>
/// <param name="Root">The id of the playlist the copy was first made as, shared by every copy of it.</param>
/// <param name="Server">The key of the server the copy came from.</param>
public sealed record PlaylistCopy(string Root, string Server);
