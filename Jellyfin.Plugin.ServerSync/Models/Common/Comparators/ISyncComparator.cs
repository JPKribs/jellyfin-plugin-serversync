namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares two values of type <typeparamref name="T"/> and computes a stable fingerprint of one, for <see cref="SyncableValue{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the values compared.</typeparam>
public interface ISyncComparator<T>
{
    /// <summary>
    /// Whether two values mean the same. Each implementation decides what that takes.
    /// </summary>
    /// <param name="source">The value read from the source.</param>
    /// <param name="local">The value read from this server.</param>
    /// <returns><c>true</c> when the two mean the same.</returns>
    bool Equals(T? source, T? local);

    /// <summary>
    /// Computes a stable fingerprint of a value, to test against a hash stored earlier on this server.
    /// It is never compared with another server's hash.
    /// </summary>
    /// <param name="value">The value to fingerprint.</param>
    /// <returns>A lowercase hex hash, or null when the value is empty or cannot be hashed.</returns>
    string? ComputeHash(T? value);
}
