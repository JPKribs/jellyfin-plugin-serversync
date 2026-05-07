using System;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// One comparable field on a <see cref="SyncRecord"/>. Holds the snapshot
/// values for source, local, and last-synced sides of a single value, plus
/// content fingerprints used to short-circuit semantic comparison.
/// <para>
/// The fast path is <c>SourceHash == SyncedHash</c>: when the source has not
/// moved since we last successfully synced it, we can skip the more expensive
/// <see cref="ISyncComparator{T}.Equals"/> call entirely. Hashes are produced
/// by the same code on both sides over time, so they compare reliably to
/// themselves; we never compare a source-side hash against a local-side hash,
/// since their serialization paths can differ at the byte level even when
/// semantically equal.
/// </para>
/// </summary>
/// <typeparam name="T">Value type, typically <see cref="string"/> for JSON blobs
/// or a primitive for field-level comparison.</typeparam>
public sealed class SyncableValue<T>
{
    /// <summary>
    /// Gets or sets the value as observed on the source server during the most
    /// recent Refresh.
    /// </summary>
    public T? Source { get; set; }

    /// <summary>
    /// Gets or sets the value as observed on the local Jellyfin instance
    /// during the most recent Refresh.
    /// </summary>
    public T? Local { get; set; }

    /// <summary>
    /// Gets or sets the value that was last successfully applied to local.
    /// Used as the baseline for the hash short-circuit.
    /// </summary>
    public T? Synced { get; set; }

    /// <summary>
    /// Gets or sets the content fingerprint of <see cref="Source"/>, recomputed
    /// each Refresh.
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>
    /// Gets or sets the content fingerprint of <see cref="Source"/> at the
    /// time of the most recent successful Sync. When equal to
    /// <see cref="SourceHash"/> the source has not moved and we can short-
    /// circuit the comparison.
    /// </summary>
    public string? SyncedHash { get; set; }

    /// <summary>
    /// Gets the comparator used to test <see cref="Equals"/> and produce
    /// <see cref="ComputeHash"/>. Must be supplied at construction.
    /// </summary>
    public required ISyncComparator<T> Comparator { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Source"/> differs meaningfully
    /// from <see cref="Local"/>. Uses the hash short-circuit when both source
    /// and synced hashes are known and equal; otherwise falls through to the
    /// comparator's <see cref="ISyncComparator{T}.Equals"/>.
    /// </summary>
    public bool HasChanges
    {
        get
        {
            if (!string.IsNullOrEmpty(SourceHash)
                && string.Equals(SourceHash, SyncedHash, StringComparison.Ordinal))
            {
                return false;
            }

            return !Comparator.Equals(Source, Local);
        }
    }

    /// <summary>
    /// Recomputes <see cref="SourceHash"/> from the current <see cref="Source"/>.
    /// Call after assigning a new <see cref="Source"/> during Refresh.
    /// </summary>
    public void RecomputeSourceHash() => SourceHash = Comparator.ComputeHash(Source);

    /// <summary>
    /// Assigns a new source value and recomputes <see cref="SourceHash"/>
    /// in one step. The canonical "I just observed a new source value"
    /// path during Refresh — equivalent to setting <see cref="Source"/>
    /// and then calling <see cref="RecomputeSourceHash"/>.
    /// </summary>
    public void UpdateSource(T? value)
    {
        Source = value;
        RecomputeSourceHash();
    }

    /// <summary>
    /// Marks the current <see cref="Source"/> as successfully applied: copies
    /// <see cref="Source"/> into <see cref="Synced"/> and <see cref="SourceHash"/>
    /// into <see cref="SyncedHash"/>. Call from the Sync phase after a
    /// successful apply.
    /// </summary>
    public void MarkSynced()
    {
        Synced = Source;
        SyncedHash = SourceHash;
    }
}
