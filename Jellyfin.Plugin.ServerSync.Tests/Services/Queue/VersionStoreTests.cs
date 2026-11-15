using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The version store keeps one stamp for each kind and key, reads many at once, and removes only what it is told to.
/// </summary>
public sealed class VersionStoreTests : IDisposable
{
    private static readonly DateTime At = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly VersionStore _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionStoreTests"/> class.
    /// </summary>
    public VersionStoreTests()
    {
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static ObjectVersion Version(string key, HintKind kind = HintKind.Metadata, string server = "peer", DateTime? at = null)
        => new() { Kind = kind, Key = key, ServerId = server, Timestamp = at ?? At };

    /// <summary>
    /// A missing stamp or key list is refused.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference under the write lock.
    /// </summary>
    [Fact]
    public void Calls_NullArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _versions.Set(null!));
        Assert.Throws<ArgumentNullException>(() => _versions.SetPending(null!));
        Assert.Throws<ArgumentNullException>(() => _versions.GetMany(HintKind.Metadata, null!));
        Assert.Throws<ArgumentNullException>(() => _versions.Remove(HintKind.Metadata, null!));
    }

    /// <summary>
    /// The same key under two kinds holds two stamps.
    /// True: an item's metadata stamp never decides its content or history.
    /// False: an edit to one sync type wins conflicts in another.
    /// </summary>
    [Fact]
    public void Get_SameKeyUnderAnotherKind_IsSeparate()
    {
        _versions.Set(Version("item", HintKind.Metadata, "a"));
        _versions.Set(Version("item", HintKind.Content, "b"));

        Assert.Equal("a", _versions.Get(HintKind.Metadata, "item")?.ServerId);
        Assert.Equal("b", _versions.Get(HintKind.Content, "item")?.ServerId);
        Assert.Null(_versions.Get(HintKind.History, "item"));
    }

    /// <summary>
    /// A stamp reads back with the kind, key, server, and UTC time it was stored with.
    /// True: a decision reads exactly the edit that was recorded.
    /// False: a stamp loses its time zone and compares hours off.
    /// </summary>
    [Fact]
    public void Get_StoredStamp_ReadsBackEveryField()
    {
        _versions.Set(Version("item", HintKind.People, "peer"));

        var stored = _versions.Get(HintKind.People, "item");

        Assert.NotNull(stored);
        Assert.Equal(HintKind.People, stored!.Kind);
        Assert.Equal("item", stored.Key);
        Assert.Equal("peer", stored.ServerId);
        Assert.Equal(At, stored.Timestamp);
        Assert.Equal(DateTimeKind.Utc, stored.Timestamp.Kind);
    }

    /// <summary>
    /// The same stamp set twice is stored once and refused the second time.
    /// True: a hint delivered twice records nothing new.
    /// False: a repeat counts as a newer edit and is announced again.
    /// </summary>
    [Fact]
    public void Set_SameStampTwice_IsRefusedTheSecondTime()
    {
        var first = _versions.Set(Version("item"));

        var second = _versions.Set(Version("item"));

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// No keys read no stamps.
    /// True: a peer asking about nothing gets an empty answer.
    /// False: an empty list builds a malformed query and the request fails.
    /// </summary>
    [Fact]
    public void GetMany_NoKeys_ReturnsNothing()
    {
        _versions.Set(Version("item"));

        var found = _versions.GetMany(HintKind.Metadata, Array.Empty<string>());

        Assert.Empty(found);
    }

    /// <summary>
    /// Repeated and empty keys are asked for once or not at all.
    /// True: each stamp comes back once.
    /// False: a peer that repeats a key gets it twice and its map of versions throws.
    /// </summary>
    [Fact]
    public void GetMany_RepeatedAndEmptyKeys_ReturnsEachStampOnce()
    {
        _versions.Set(Version("a"));
        _versions.Set(Version("b"));

        var found = _versions.GetMany(HintKind.Metadata, new[] { "a", "a", string.Empty, "b", "missing" });

        Assert.Equal(new[] { "a", "b" }, found.Select(v => v.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// More keys than fit one statement are read in chunks, and none are lost.
    /// True: a peer may ask for hundreds of stamps at once.
    /// False: a large request passes SQLite's parameter limit and fails, or drops the keys past the first chunk.
    /// </summary>
    [Fact]
    public void GetMany_MoreKeysThanOneChunk_ReturnsThemAll()
    {
        var keys = Enumerable.Range(0, 450).Select(i => "k" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        foreach (var key in keys)
        {
            _versions.Set(Version(key));
        }

        var found = _versions.GetMany(HintKind.Metadata, keys);

        Assert.Equal(450, found.Count);
        Assert.Equal(450, found.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Only the stamps of the kind asked for come back.
    /// True: a history request never answers with a metadata stamp.
    /// False: stamps leak across sync types.
    /// </summary>
    [Fact]
    public void GetMany_AnotherKindHoldsTheKey_ReturnsNothing()
    {
        _versions.Set(Version("item", HintKind.Content));

        var found = _versions.GetMany(HintKind.Metadata, new[] { "item" });

        Assert.Empty(found);
    }

    /// <summary>
    /// The keys of one kind are listed, and no others.
    /// True: pruning walks only the kind it was asked about.
    /// False: a metadata prune deletes history stamps.
    /// </summary>
    [Fact]
    public void GetKeys_SeveralKinds_ListsOnlyTheKindAsked()
    {
        _versions.Set(Version("m1", HintKind.Metadata));
        _versions.Set(Version("m2", HintKind.Metadata));
        _versions.Set(Version("h1", HintKind.History));

        var keys = _versions.GetKeys(HintKind.Metadata);

        Assert.Equal(new[] { "m1", "m2" }, keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Empty(_versions.GetKeys(HintKind.People));
    }

    /// <summary>
    /// Removing keys removes those stamps of that kind and counts them.
    /// True: stamps of deleted items go, and everything else stays.
    /// False: a prune removes the wrong kind, or a key that was never named.
    /// </summary>
    [Fact]
    public void Remove_NamedKeys_RemovesOnlyThoseOfThatKind()
    {
        _versions.Set(Version("gone", HintKind.Metadata));
        _versions.Set(Version("kept", HintKind.Metadata));
        _versions.Set(Version("gone", HintKind.Content));

        var removed = _versions.Remove(HintKind.Metadata, new[] { "gone", "never-there" });

        Assert.Equal(1, removed);
        Assert.Null(_versions.Get(HintKind.Metadata, "gone"));
        Assert.NotNull(_versions.Get(HintKind.Metadata, "kept"));
        Assert.NotNull(_versions.Get(HintKind.Content, "gone"));
    }

    /// <summary>
    /// Removing no keys removes nothing.
    /// True: an upkeep run with nothing gone changes nothing.
    /// False: an empty list clears the table.
    /// </summary>
    [Fact]
    public void Remove_NoKeys_RemovesNothing()
    {
        _versions.Set(Version("item"));

        var removed = _versions.Remove(HintKind.Metadata, Array.Empty<string>());

        Assert.Equal(0, removed);
        Assert.NotNull(_versions.Get(HintKind.Metadata, "item"));
    }

    /// <summary>
    /// A second pending stamp for a key replaces the first.
    /// True: the stamp promoted after an apply is the one read with the latest value.
    /// False: a stale pending stamp is recorded for a newer value.
    /// </summary>
    [Fact]
    public void SetPending_Twice_KeepsTheLater()
    {
        _versions.SetPending(Version("item", server: "first"));
        _versions.SetPending(Version("item", server: "second", at: At.AddMinutes(1)));

        _versions.PromotePending(HintKind.Metadata, "item");

        var stored = _versions.Get(HintKind.Metadata, "item");
        Assert.Equal("second", stored?.ServerId);
        Assert.Equal(At.AddMinutes(1), stored?.Timestamp);
    }

    /// <summary>
    /// Promoting a pending stamp older than the one held forgets it and leaves the held stamp.
    /// True: a late apply of an old value cannot move the stamp backwards.
    /// False: the store says an older edit is current and the next conflict goes the wrong way.
    /// </summary>
    [Fact]
    public void PromotePending_OlderThanTheHeldStamp_KeepsTheHeldStamp()
    {
        _versions.Set(Version("item", server: "newer", at: At.AddHours(1)));
        _versions.SetPending(Version("item", server: "older", at: At));

        var promoted = _versions.PromotePending(HintKind.Metadata, "item");

        Assert.True(promoted);
        Assert.Equal("newer", _versions.Get(HintKind.Metadata, "item")?.ServerId);
        Assert.False(_versions.PromotePending(HintKind.Metadata, "item"));
    }

    /// <summary>
    /// A pending stamp dated ahead of this server's clock is stored as the current time.
    /// True: a peer's fast clock cannot plant a stamp that beats every later edit.
    /// False: one promoted future stamp wins every conflict for that object.
    /// </summary>
    [Fact]
    public void SetPending_FutureStamp_IsBoundedToNow()
    {
        _versions.SetPending(Version("item", at: new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        _versions.PromotePending(HintKind.Metadata, "item");

        var stored = _versions.Get(HintKind.Metadata, "item");
        Assert.NotNull(stored);
        Assert.True(stored!.Timestamp <= DateTime.UtcNow);
    }

    /// <summary>
    /// Pending stamps read before the cutoff are removed, and later ones stay.
    /// True: a value that never applied does not leave its stamp behind forever.
    /// False: the table grows for good, or a stamp still waiting for its apply is thrown away.
    /// </summary>
    [Fact]
    public void PrunePending_CutoffBeforeAndAfterTheRead_RemovesOnlyOlderReads()
    {
        _versions.SetPending(Version("item"));

        var beforeTheRead = _versions.PrunePending(DateTime.UtcNow.AddDays(-1));
        var afterTheRead = _versions.PrunePending(DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(0, beforeTheRead);
        Assert.Equal(1, afterTheRead);
        Assert.False(_versions.PromotePending(HintKind.Metadata, "item"));
    }

    /// <summary>
    /// Clearing a pending stamp that is not there changes nothing.
    /// True: a scan may clear every row it reads without checking first.
    /// False: the clear throws and stops the scan.
    /// </summary>
    [Fact]
    public void ClearPending_NothingPending_DoesNothing()
    {
        _versions.Set(Version("item"));

        _versions.ClearPending(HintKind.Metadata, "item");

        Assert.NotNull(_versions.Get(HintKind.Metadata, "item"));
    }
}
