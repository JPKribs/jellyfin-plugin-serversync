namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Strategy interface for comparing two values of type <typeparamref name="T"/>
/// and computing a stable content fingerprint of one. Used by
/// <see cref="SyncableValue{T}"/> to keep comparison and hashing logic out of
/// per-module code.
/// </summary>
/// <typeparam name="T">Value type being compared.</typeparam>
public interface ISyncComparator<T>
{
    /// <summary>
    /// Returns true if <paramref name="source"/> and <paramref name="local"/>
    /// are semantically equivalent. Implementations decide what "equivalent"
    /// means (raw equality, JSON equality with timezone tolerance, etc.).
    /// </summary>
    /// <param name="source">Value as fetched from the source server.</param>
    /// <param name="local">Value as observed on the local server.</param>
    /// <returns>True if equivalent.</returns>
    bool Equals(T? source, T? local);

    /// <summary>
    /// Returns a stable content fingerprint of <paramref name="value"/>.
    /// Returns null when the value is empty or hashing is not meaningful for
    /// this comparator. Used as a fast-path equality test against a previously
    /// stored hash; never used as a cross-server comparator.
    /// </summary>
    /// <param name="value">Value to fingerprint.</param>
    /// <returns>Lowercase hex hash, or null for empty/non-hashable inputs.</returns>
    string? ComputeHash(T? value);
}
