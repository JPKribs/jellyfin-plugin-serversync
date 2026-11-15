namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>What one check of this server's links found.</summary>
/// <param name="Checked">How many links were checked.</param>
/// <param name="Dead">How many links cannot play.</param>
/// <param name="Restored">How many flagged links can play again.</param>
/// <param name="Described">How many links were given the source's media info.</param>
public sealed record LinkVerification(int Checked, int Dead, int Restored, int Described);
