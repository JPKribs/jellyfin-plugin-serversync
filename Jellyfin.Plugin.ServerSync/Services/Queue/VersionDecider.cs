using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Decides between a local value and one a peer offers by their stamps, never by saved dates.
/// Equal values end most echoes without a write, the newer stamp wins otherwise, and the server id breaks a tie.
/// </summary>
public static class VersionDecider
{
    /// <summary>Decides what to do with an incoming value.</summary>
    /// <param name="local">The version this server holds, or null when the object was never versioned here.</param>
    /// <param name="incoming">The version the peer's value carries.</param>
    /// <param name="valuesEqual">Whether the two values are already the same.</param>
    /// <returns>The decision.</returns>
    public static VersionDecision Decide(ObjectVersion? local, ObjectVersion incoming, bool valuesEqual)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (valuesEqual)
        {
            return VersionDecision.Adopt;
        }

        if (local is null)
        {
            return VersionDecision.Apply;
        }

        // Same edit with different values means a copy drifted, so the arrival wins.
        if (SameVersion(local, incoming))
        {
            return VersionDecision.Apply;
        }

        return IsNewer(incoming, local) ? VersionDecision.Apply : VersionDecision.Keep;
    }

    /// <summary>Whether two stamps name the same edit, which is the same server at the same instant.</summary>
    /// <param name="a">One stamp to compare.</param>
    /// <param name="b">The stamp to compare it with.</param>
    /// <returns><c>true</c> when they are the same edit.</returns>
    public static bool SameVersion(ObjectVersion a, ObjectVersion b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return UtcTime.AsUtc(a.Timestamp) == UtcTime.AsUtc(b.Timestamp) && string.Equals(a.ServerId, b.ServerId, StringComparison.Ordinal);
    }

    /// <summary>Whether one version is newer than another, with the server id breaking ties.</summary>
    /// <param name="candidate">The version being tested.</param>
    /// <param name="other">The version it is compared to.</param>
    /// <returns><c>true</c> when the candidate wins.</returns>
    public static bool IsNewer(ObjectVersion candidate, ObjectVersion other)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(other);

        var byTime = UtcTime.AsUtc(candidate.Timestamp).CompareTo(UtcTime.AsUtc(other.Timestamp));
        if (byTime != 0)
        {
            return byTime > 0;
        }

        return string.CompareOrdinal(candidate.ServerId, other.ServerId) > 0;
    }
}
