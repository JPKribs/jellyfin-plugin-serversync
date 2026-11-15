using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Another server a link can fall back to, and the path the same file has there.</summary>
/// <param name="Server">The server entry to fall back to.</param>
/// <param name="SourcePath">The path the file has on that server.</param>
public sealed record LinkAlternate(SourceServer Server, string SourcePath);
