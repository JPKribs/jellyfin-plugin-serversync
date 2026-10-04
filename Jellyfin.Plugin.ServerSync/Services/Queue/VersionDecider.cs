using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What to do with an incoming value, given the versions on both sides.</summary>
public enum VersionDecision
{
    /// <summary>The values already match. Write nothing and adopt the incoming version.</summary>
    Adopt,

    /// <summary>The incoming edit is newer. Write it and carry its version.</summary>
    Apply,

    /// <summary>The local edit is newer. Keep it, and tell the peers about it.</summary>
    Keep
}

/// <summary>
/// Decides between a local value and one offered by a peer on origin versions, never on saved dates.
/// Content equality is checked first because it ends most loops on its own: a copy that already
/// matches needs no write and raises no hint. When the values differ the newer origin timestamp wins,
/// and a tie breaks on the origin server id so two servers always reach the same answer.
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

        // Both sides claim the same edit but hold different values: one copy drifted, through provider
        // work, a field the apply cannot write, or an image that compares differently. Keeping would
        // leave each side sending the same version to the other forever, so the copy arriving wins.
        if (SameVersion(local, incoming))
        {
            return VersionDecision.Apply;
        }

        return IsNewer(incoming, local) ? VersionDecision.Apply : VersionDecision.Keep;
    }

    /// <summary>Whether two versions name the same edit: the same server at the same instant.</summary>
    /// <param name="a">One version.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they are the same edit.</returns>
    public static bool SameVersion(ObjectVersion a, ObjectVersion b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Utilities.UtcTime.AsUtc(a.Timestamp) == Utilities.UtcTime.AsUtc(b.Timestamp) && string.Equals(a.ServerId, b.ServerId, StringComparison.Ordinal);
    }

    /// <summary>Whether one version is newer than another, with the server id breaking ties.</summary>
    /// <param name="candidate">The version being tested.</param>
    /// <param name="other">The version it is compared to.</param>
    /// <returns><c>true</c> when the candidate wins.</returns>
    public static bool IsNewer(ObjectVersion candidate, ObjectVersion other)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(other);

        var byTime = Utilities.UtcTime.AsUtc(candidate.Timestamp).CompareTo(Utilities.UtcTime.AsUtc(other.Timestamp));
        if (byTime != 0)
        {
            return byTime > 0;
        }

        return string.CompareOrdinal(candidate.ServerId, other.ServerId) > 0;
    }

}
