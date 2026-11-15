namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>The resolved step for one row.</summary>
/// <param name="Action">What to do next.</param>
/// <param name="Reason">Why, when the action is a failure.</param>
public readonly record struct NegotiationStep(NegotiationAction Action, string? Reason);
