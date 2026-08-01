using System;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// One comparable field on a <see cref="SyncRecord"/>: source / local /
/// synced snapshots, plus content fingerprints recording what was last
/// applied. Change detection compares source against local through the
/// comparator — see <see cref="HasChanges"/>.
/// </summary>
/// <typeparam name="T">Value type, typically <see cref="string"/> for JSON blobs.</typeparam>
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
    /// Recorded for display and per-module bookkeeping; not consulted by
    /// <see cref="HasChanges"/>.
    /// </summary>
    public T? Synced { get; set; }

    /// <summary>
    /// Gets or sets the content fingerprint of <see cref="Source"/>, recomputed
    /// each Refresh.
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>
    /// Gets or sets the content fingerprint of <see cref="Source"/> at the
    /// time of the most recent successful Sync. Equality with
    /// <see cref="SourceHash"/> tells you the source has not moved since that
    /// apply — which is NOT the same as local still matching it, so this is
    /// no longer used to skip comparison. See <see cref="HasChanges"/>.
    /// </summary>
    public string? SyncedHash { get; set; }

    /// <summary>
    /// Gets the comparator used to test <see cref="Equals"/> and produce
    /// <see cref="ComputeHash"/>. Must be supplied at construction.
    /// </summary>
    public required ISyncComparator<T> Comparator { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Source"/> differs meaningfully
    /// from <see cref="Local"/>, per the comparator.
    /// <para>
    /// This used to short-circuit to <c>false</c> whenever
    /// <see cref="SourceHash"/> equalled <see cref="SyncedHash"/>. That asks
    /// "has the source moved since the last apply?", which is not the same
    /// question: local can drift on its own. Because the refresh calls
    /// <c>MarkSynced</c> on any row where source already matched local, nearly
    /// every row carried a baseline — so a later local edit (an overview
    /// rewritten, a tag dropped, an image replaced on this server) was invisible
    /// until the source item happened to change.
    /// </para>
    /// <para>
    /// Restoring the fast path correctly would need the local fingerprint at
    /// apply time persisted alongside <see cref="SyncedHash"/>. It is not worth
    /// it: <see cref="ISyncComparator{T}.ComputeHash"/> is a raw-bytes digest
    /// and is documented as unstable across code paths, and the refresh
    /// materializes both blobs before consulting this property anyway — the
    /// short-circuit only ever skipped a comparison of two strings already in
    /// memory. <see cref="SyncedHash"/> is still maintained for the modal and
    /// for module-specific bookkeeping.
    /// </para>
    /// </summary>
    public bool HasChanges => !Comparator.Equals(Source, Local);

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
