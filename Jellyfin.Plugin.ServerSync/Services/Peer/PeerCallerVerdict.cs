using System;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>The decision on one caller of a peer route.</summary>
/// <param name="Code">Why the caller was let in or turned away.</param>
/// <param name="Refusal">The reason to show a refused caller, or null when allowed.</param>
/// <param name="ScopedUserId">The user whose view the caller is limited to, or null for the whole library.</param>
public readonly record struct PeerCallerVerdict(PeerCallerCode Code, string? Refusal, Guid? ScopedUserId)
{
    /// <summary>Builds a verdict that lets the caller in.</summary>
    /// <param name="scopedUserId">The user whose view the caller is limited to, or null for the whole library.</param>
    /// <returns>The verdict.</returns>
    public static PeerCallerVerdict Allow(Guid? scopedUserId) => new(PeerCallerCode.Allowed, null, scopedUserId);

    /// <summary>Builds a verdict that turns the caller away.</summary>
    /// <param name="code">Why the caller is refused.</param>
    /// <param name="reason">The reason to show the caller.</param>
    /// <returns>The verdict.</returns>
    public static PeerCallerVerdict Refuse(PeerCallerCode code, string reason) => new(code, reason, null);
}
