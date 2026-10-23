using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Playlists;

/// <summary>
/// The registry of playlists this server made as copies of another server's, against a real SQLite
/// file. It is what tells a copy (which the mirror may rewrite and the prune may delete) apart from a
/// user's own playlist (which neither may touch), so a lost or wrong entry is a deleted playlist.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class PlaylistCopyStoreTests : IDisposable
{
    private readonly PlaylistTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>
    /// A registered copy reads back with its root and server, and an unregistered playlist reads as null.
    /// True: the mirror recognises the copies it made.
    /// False: a copy reads as a user's own playlist and is never updated, or a user's playlist reads as a copy.
    /// </summary>
    [Fact]
    public void SetThenGet_RoundTrips_AndUnknownIsNull()
    {
        var id = Guid.NewGuid();

        PlaylistCopyStore.Set(id, "root-1", "server-a");

        Assert.Equal(new PlaylistCopy("root-1", "server-a"), PlaylistCopyStore.Get(id));
        Assert.Null(PlaylistCopyStore.Get(Guid.NewGuid()));
    }

    /// <summary>
    /// Registering the same playlist again replaces its root and server rather than adding a row.
    /// True: a copy that moves to another server or root is tracked once, under the new values.
    /// False: duplicate rows, or the first registration wins and the copy is pruned against the wrong server.
    /// </summary>
    [Fact]
    public void Set_Again_ReplacesTheEntry()
    {
        var id = Guid.NewGuid();
        PlaylistCopyStore.Set(id, "root-1", "server-a");

        PlaylistCopyStore.Set(id, "root-2", "server-b");

        Assert.Equal(new PlaylistCopy("root-2", "server-b"), PlaylistCopyStore.Get(id));
        Assert.Equal(1, PlaylistCopyStore.Count());
    }

    /// <summary>
    /// Remove drops one entry, and Count and Ids list what is left.
    /// True: a deleted copy is forgotten and the rest stay registered.
    /// False: a deleted copy's id lingers, or removing one forgets others.
    /// </summary>
    [Fact]
    public void Remove_DropsOne_CountAndIdsListTheRest()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        PlaylistCopyStore.Set(a, "r", "s");
        PlaylistCopyStore.Set(b, "r", "s");
        PlaylistCopyStore.Set(c, "r", "s");

        PlaylistCopyStore.Remove(b);
        PlaylistCopyStore.Remove(Guid.NewGuid());

        Assert.Equal(2, PlaylistCopyStore.Count());
        Assert.Equal(new[] { a, c }.OrderBy(g => g), PlaylistCopyStore.Ids().OrderBy(g => g));
        Assert.Null(PlaylistCopyStore.Get(b));
    }

    /// <summary>
    /// Entries live in the database, so a new store over the same database, as after a restart, still has them.
    /// True: copies stay recognised across a restart.
    /// False: after a restart every copy reads as a user's playlist and duplicates are made on the next run.
    /// </summary>
    [Fact]
    public void Entries_SurviveANewStoreOverTheSameDatabase()
    {
        var id = Guid.NewGuid();
        PlaylistCopyStore.Set(id, "root-1", "server-a");

        _ = new PlaylistCopyStore(new ReopenedProvider(_h.Database), NullLogger<PlaylistCopyStore>.Instance);

        Assert.Equal(new PlaylistCopy("root-1", "server-a"), PlaylistCopyStore.Get(id));
    }

    private sealed class ReopenedProvider : Jellyfin.Plugin.ServerSync.Services.ISyncDatabaseProvider
    {
        public ReopenedProvider(Jellyfin.Plugin.ServerSync.Services.SyncDatabase db) => Database = db;

        public Jellyfin.Plugin.ServerSync.Services.SyncDatabase Database { get; }
    }
}
