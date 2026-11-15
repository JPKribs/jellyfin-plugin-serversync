namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>What one check of this server's links found.</summary>
public sealed record LinkVerification(int Checked, int Dead, int Restored, int Described);
