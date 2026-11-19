using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The reader must answer every named item in order, with every segment it holds and the server that first made them when Server Sync copied them here.
/// </summary>
public sealed class PeerSegmentReaderTests : IDisposable
{
    private static readonly Guid FilmId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid ShowId = Guid.Parse("22222222222222222222222222222222");

    private readonly List<LibraryOptions> _askedWith = new();
    private bool _copied;
    private readonly DatabaseTempFolder _folder = new("segment-reader");
    private readonly SyncDatabase _database;
    private readonly MediaSegmentStore _store;
    private readonly PeerSegmentReader _reader;
    private readonly InterfaceStub _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSegmentReaderTests"/> class with a library that holds one film with an intro, and one show.
    /// </summary>
    public PeerSegmentReaderTests()
    {
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == FilmId
            ? new Movie { Id = FilmId, Size = 5000, RunTimeTicks = 900 }
            : (Guid)args[0]! == ShowId ? new MediaBrowser.Controller.Entities.TV.Series { Id = ShowId } : null);
        libraryStub.Answer(nameof(ILibraryManager.GetLibraryOptions), _ => new LibraryOptions { DisabledMediaSegmentProviders = new[] { "Other" } });
        var (segments, segmentsStub) = InterfaceStub.Create<IMediaSegmentManager>();
        segmentsStub.Answer(nameof(IMediaSegmentManager.GetSegmentsAsync), args =>
        {
            var options = (LibraryOptions)args[2]!;
            _askedWith.Add(options);
            var othersOnly = options.DisabledMediaSegmentProviders.Contains("Server Sync");
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(othersOnly && _copied
                ? Array.Empty<MediaSegmentDto>()
                : new[] { new MediaSegmentDto { ItemId = FilmId, Type = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 } });
        });
        var (users, usersStub) = InterfaceStub.Create<IUserManager>();
        _users = usersStub;
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _store = new MediaSegmentStore(new FixedProvider(_database), NullLogger<MediaSegmentStore>.Instance);
        _reader = new PeerSegmentReader(library, users, segments, _store);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when Jellyfin builds its services.
    /// False: the first read would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        var library = InterfaceStub.Create<ILibraryManager>().Proxy;
        var users = InterfaceStub.Create<IUserManager>().Proxy;
        var segments = InterfaceStub.Create<IMediaSegmentManager>().Proxy;

        Assert.Throws<ArgumentNullException>(() => new PeerSegmentReader(null!, users, segments, _store));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentReader(library, null!, segments, _store));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentReader(library, users, null!, _store));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentReader(library, users, segments, null!));
    }

    /// <summary>
    /// A film's segments are read with its size and runtime, through the providers its library has on, Server Sync included, and with no origin when none was copied here.
    /// True: the caller gets every segment this server shows, and takes this server as their origin.
    /// False: a server that copied segments could not pass them on, or one that made them would be credited to another.
    /// </summary>
    [Fact]
    public async Task ReadAsync_Film_GivesItsSegmentsWithTheFile()
    {
        var answer = await _reader.ReadAsync(new[] { FilmId.ToString("N") }, null);

        var item = Assert.Single(answer.Items);
        Assert.True(item.Found);
        Assert.Equal(5000, item.Size);
        Assert.Equal(900, item.RunTimeTicks);
        Assert.Null(item.OriginServerId);
        var segment = Assert.Single(item.Segments);
        Assert.Equal(("Intro", 10L, 20L), (segment.Type, segment.StartTicks, segment.EndTicks));
        Assert.Equal(new[] { "Other" }, _askedWith[0].DisabledMediaSegmentProviders);
    }

    /// <summary>
    /// A film whose segments Server Sync gave it names the server they were first made on, and one whose segments were made here names none, whatever the cache still holds.
    /// True: the next server keeps the true origin, and segments made here are never credited to another server.
    /// False: a deleted intro could return to the server that made it, or a server's own intro would be refused by the server a stale cache row names.
    /// </summary>
    [Theory]
    [InlineData(true, "first-id")]
    [InlineData(false, null)]
    public async Task ReadAsync_Segments_NameTheirOriginOnlyWhenCopied(bool copied, string? origin)
    {
        _copied = copied;
        _store.Save(new[] { new Jellyfin.Plugin.ServerSync.Models.MetadataSync.SourceMediaSegments(FilmId.ToString("N"), "a", "x", Array.Empty<Jellyfin.Plugin.ServerSync.Models.Peer.PeerSegment>(), null, null, DateTime.UtcNow, "first-id") });

        var answer = await _reader.ReadAsync(new[] { FilmId.ToString("N") }, null);

        Assert.Equal(origin, Assert.Single(answer.Items).OriginServerId);
    }

    /// <summary>
    /// Every named item is answered in order, and an id that is not one, an item not here, or a folder is not found.
    /// True: the caller matches each answer to its request and forgets only what this server lacks.
    /// False: a bad id would fail the batch, or a show would be read as a file.
    /// </summary>
    [Fact]
    public async Task ReadAsync_Mixed_AnswersEachInOrder()
    {
        var ids = new[] { "not-an-id", Guid.NewGuid().ToString("N"), ShowId.ToString("N"), FilmId.ToString() };

        var answer = await _reader.ReadAsync(ids, null);

        Assert.Equal(ids, answer.Items.Select(i => i.ItemId));
        Assert.Equal(new[] { false, false, false, true }, answer.Items.Select(i => i.Found));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _reader.ReadAsync(null!, null));
    }

    /// <summary>
    /// A standard user whose account is gone is told nothing.
    /// True: a scoped key cannot learn what this server holds unless its user can see it.
    /// False: a standard user's key could read every file's segments.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ScopedUserGone_FindsNothing()
    {
        var answer = await _reader.ReadAsync(new[] { FilmId.ToString("N") }, Guid.NewGuid());

        Assert.False(Assert.Single(answer.Items).Found);
        Assert.Empty(_askedWith);
        Assert.Contains(_users.Calls, c => c.Name == nameof(IUserManager.GetUserById));
    }
}
