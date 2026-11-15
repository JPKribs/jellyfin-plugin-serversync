using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Another server a link can fall back to, and the path the same file has there.</summary>
public sealed record LinkAlternate(SourceServer Server, string SourcePath);
