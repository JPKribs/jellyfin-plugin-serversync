using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// One comparable field on a <see cref="SyncRecord"/>, holding its source, local, and synced values with fingerprints of what was last applied.
/// </summary>
/// <typeparam name="T">The type of the value, which is <see cref="string"/> for a JSON blob.</typeparam>
public sealed class SyncableValue<T>
{
    /// <summary>
    /// Gets or sets the value the last scan read from the source.
    /// </summary>
    public T? Source { get; set; }

    /// <summary>
    /// Gets or sets the value the last scan read from this server.
    /// </summary>
    public T? Local { get; set; }

    /// <summary>
    /// Gets or sets the value last applied to this server. <see cref="HasChanges"/> does not read it.
    /// </summary>
    public T? Synced { get; set; }

    /// <summary>
    /// Gets or sets the fingerprint of <see cref="Source"/>, computed again by each scan.
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>
    /// Gets or sets the fingerprint of <see cref="Source"/> at the last successful apply.
    /// Matching <see cref="SourceHash"/> only means the source has not moved, so it never skips a comparison.
    /// </summary>
    public string? SyncedHash { get; set; }

    /// <summary>
    /// Gets the comparator that tests equality and computes fingerprints.
    /// </summary>
    public required ISyncComparator<T> Comparator { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Source"/> differs from <see cref="Local"/>, per the comparator.
    /// It always compares the values, since matching hashes would hide an edit made on this server.
    /// </summary>
    public bool HasChanges => !Comparator.Equals(Source, Local);

    /// <summary>
    /// Computes <see cref="SourceHash"/> again from the current <see cref="Source"/>.
    /// </summary>
    public void RecomputeSourceHash() => SourceHash = Comparator.ComputeHash(Source);

    /// <summary>
    /// Assigns a new source value and computes <see cref="SourceHash"/> from it.
    /// </summary>
    /// <param name="value">The value the scan read from the source.</param>
    public void UpdateSource(T? value)
    {
        Source = value;
        RecomputeSourceHash();
    }

    /// <summary>
    /// Records the current <see cref="Source"/> and its fingerprint as applied.
    /// </summary>
    public void MarkSynced()
    {
        Synced = Source;
        SyncedHash = SourceHash;
    }
}
