using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.MetadataSync;

/// <summary>
/// The store must list only the video and audio files the Metadata table matched, keep each file's segments whole, and forget them only for files no longer matched.
/// </summary>
public sealed class MediaSegmentStoreTests : IDisposable
{
    private static readonly DateTime At = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    private readonly DatabaseTempFolder _folder = new("segment-store");
    private readonly SyncDatabase _database;
    private readonly MetadataSyncTableManager _table;
    private readonly MediaSegmentStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentStoreTests"/> class over an empty database.
    /// </summary>
    public MediaSegmentStoreTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        var provider = new FixedProvider(_database);
        _table = new MetadataSyncTableManager(provider, NullLogger<MetadataSyncTableManager>.Instance);
        _store = new MediaSegmentStore(provider, NullLogger<MediaSegmentStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// Candidates are the matched video and audio files with a local item and a server, never folders, other types, or unmatched rows.
    /// True: the scan asks the sources only about files that can hold segments and exist here.
    /// False: the scan would ask about series and photos, or about files this server does not hold.
    /// </summary>
    [Fact]
    public void Candidates_ListsMatchedVideoAndAudioFilesOnly()
    {
        Track("film", "Movie", "local-film", "a");
        Track("episode", "Episode", "local-episode", "b");
        Track("song", "Audio", "local-song", "a");
        Track("show", "Series", "local-show", "a", isFolder: true);
        Track("photo", "Photo", "local-photo", "a");
        Track("unmatched", "Movie", null, "a");
        Track("no-server", "Movie", "local-x", null);

        var candidates = _store.Candidates().OrderBy(c => c.LocalItemId, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "local-episode", "local-film", "local-song" }, candidates.Select(c => c.LocalItemId));
        Assert.Equal(new MediaSegmentCandidate("local-film", "a", "film"), candidates[1]);
    }

    /// <summary>
    /// Saved segments read back whole, a second save replaces the first, and an item with nothing saved reads as null.
    /// True: the provider gives the source's latest segments, with the file they fit.
    /// False: the provider would give stale segments, or ones that lost their times or the file's size.
    /// </summary>
    [Fact]
    public void Save_ThenGet_ReturnsTheLatest()
    {
        Assert.Null(_store.Get("local-film"));
        _store.Save(new[] { Row("local-film", "Intro") });
        _store.Save(new[] { Row("local-film", "Outro", "Recap") });

        var held = _store.Get("local-film")!;

        Assert.Equal(new[] { "Outro", "Recap" }, held.Segments.Select(s => s.Type));
        Assert.Equal(10, held.Segments[0].StartTicks);
        Assert.Equal(1234, held.Size);
        Assert.Equal(5678, held.RunTimeTicks);
        Assert.Equal(At, held.FetchedAt);
        Assert.Equal("origin-id", held.OriginServerId);
        Assert.Throws<ArgumentException>(() => _store.Get(string.Empty));
    }

    /// <summary>
    /// Remove forgets the named items, and RemoveUntracked forgets only items the Metadata table no longer holds.
    /// True: a file that left every source, or this server, stops taking segments, and every other file keeps them.
    /// False: kept segments would outlive their files, or a matched file would lose them.
    /// </summary>
    [Fact]
    public void Remove_AndRemoveUntracked_ForgetOnlyWhatIsGone()
    {
        Track("film", "Movie", "local-film", "a");
        Track("episode", "Episode", "local-episode", "a");
        _store.Save(new[] { Row("local-film"), Row("local-episode"), Row("local-gone") });

        var removed = _store.Remove(new[] { "local-episode" });
        var untracked = _store.RemoveUntracked();

        Assert.Equal(1, removed);
        Assert.Equal(1, untracked);
        Assert.NotNull(_store.Get("local-film"));
        Assert.Null(_store.Get("local-episode"));
        Assert.Null(_store.Get("local-gone"));
        Assert.Equal(0, _store.Remove(Array.Empty<string>()));
    }

    private void Track(string sourceItem, string type, string? localItem, string? server, bool isFolder = false)
        => _table.Upsert(new MetadataSyncItem
        {
            SourceLibraryId = "lib",
            LocalLibraryId = "local-lib",
            SourceItemId = sourceItem,
            LocalItemId = localItem,
            ItemType = type,
            IsFolder = isFolder,
            ServerKey = server,
            StatusDate = At
        });

    private static SourceMediaSegments Row(string localItem, params string[] types)
        => new(localItem, "a", "source-" + localItem, types.Select(t => new PeerSegment { Type = t, StartTicks = 10, EndTicks = 20 }).ToList(), 1234, 5678, At, "origin-id");
}
