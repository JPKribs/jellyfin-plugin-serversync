namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Where a link may play from, or why it may not play at all.</summary>
/// <param name="Route">The route to stream from, or null when no server allows the play.</param>
/// <param name="Refusal">Why no server allows the play, or null when one does.</param>
public sealed record LinkChoice(LinkRoute? Route, string? Refusal);
