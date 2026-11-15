namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link may play from, or why it may not play at all.</summary>
public sealed record LinkChoice(LinkRoute? Route, string? Refusal);
