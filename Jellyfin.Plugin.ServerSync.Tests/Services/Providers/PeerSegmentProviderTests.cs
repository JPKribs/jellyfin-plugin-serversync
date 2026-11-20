using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The media segment provider must answer from what the metadata scan kept, only for the same file, cut to fit it, and keep the segments here whenever it holds nothing.
/// </summary>
public sealed class PeerSegmentProviderTests : IDisposable
{
    private const long Size = 5000;
    private const long RunTime = 1000;
    private static readonly Guid LocalId = Guid.Parse("11111111111111111111111111111111");

    private readonly DatabaseTempFolder _folder = new("segment-provider");
    private readonly SyncDatabase _database;
    private readonly MediaSegmentStore _store;
    private readonly FakeConfigManager _config = new();
    private readonly PeerSegmentProvider _provider;
    private readonly IServiceProvider _services;
    private readonly List<Guid> _deleted = new();
    private readonly List<LibraryOptions> _askedWith = new();
    private List<MediaSegmentDto> _others = new();
    private readonly Movie _film = new() { Id = LocalId, Path = "/local/movies/film.mkv", RunTimeTicks = RunTime, Size = Size };

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSegmentProviderTests"/> class with the provider on and a library that holds one film.
    /// </summary>
    public PeerSegmentProviderTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _store = new MediaSegmentStore(new FixedProvider(_database), NullLogger<MediaSegmentStore>.Instance);
        _config.Configuration.EnableMetadataSync = true;
        _config.Configuration.MetadataSyncSegmentProvider = true;
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == LocalId ? _film : null);
        stub.Answer(nameof(ILibraryManager.GetLibraryOptions), _ => new LibraryOptions { DisabledMediaSegmentProviders = new[] { "Off" } });
        var (segments, segmentsStub) = InterfaceStub.Create<IMediaSegmentManager>();
        segmentsStub.Answer(nameof(IMediaSegmentManager.GetSegmentsAsync), args =>
        {
            _askedWith.Add((LibraryOptions)args[2]!);
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(_others);
        });
        segmentsStub.Answer(nameof(IMediaSegmentManager.DeleteSegmentAsync), args =>
        {
            _deleted.Add((Guid)args[0]!);
            return Task.CompletedTask;
        });
        var (services, servicesStub) = InterfaceStub.Create<IServiceProvider>();
        servicesStub.Answer(nameof(IServiceProvider.GetService), args => (Type)args[0]! == typeof(IMediaSegmentManager) ? segments : null);
        _services = services;
        _provider = new PeerSegmentProvider(_config, _store, library, _services, NullLogger<PeerSegmentProvider>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// The constructor refuses each missing dependency, and the provider is named after Server Sync.
    /// True: a library lists the provider as Server Sync, and a broken registration fails when Jellyfin builds its services.
    /// False: the provider would show under another name, or the first scan would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_ThrowsAndNameIsServerSync()
    {
        var library = InterfaceStub.Create<ILibraryManager>().Proxy;
        var logger = NullLogger<PeerSegmentProvider>.Instance;

        Assert.Throws<ArgumentNullException>(() => new PeerSegmentProvider(null!, _store, library, _services, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentProvider(_config, null!, library, _services, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentProvider(_config, _store, null!, _services, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentProvider(_config, _store, library, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerSegmentProvider(_config, _store, library, _services, null!));
        Assert.Equal("Server Sync", _provider.Name);
        Assert.Equal(int.MaxValue, _provider.Order);
    }

    /// <summary>
    /// Jellyfin asks for a video only while Enable Metadata Sync and Enable Media Segment Provider are both on, and never for a folder.
    /// True: turning either off leaves the segments here as they are, since Jellyfin skips the provider.
    /// False: turning the switch off would make Jellyfin delete every segment the provider gave.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task Supports_Switches_DecideWhetherJellyfinAsks(bool metadataSync, bool segmentProvider, bool supports)
    {
        _config.Configuration.EnableMetadataSync = metadataSync;
        _config.Configuration.MetadataSyncSegmentProvider = segmentProvider;

        Assert.Equal(supports, await _provider.Supports(_film));
        Assert.False(await _provider.Supports(new BoxSet()));
    }

    /// <summary>
    /// The kept segments are given for the local item, each with a new id, without asking a source.
    /// True: Jellyfin's scan stores the source's intro for the same file here, and makes no request to do it.
    /// False: the segments would point at the source's item, or each scan would ask the sources again.
    /// </summary>
    [Fact]
    public async Task GetMediaSegments_KeptForTheSameFile_GivesThemForTheLocalItem()
    {
        Keep(Size, RunTime, new PeerSegment { Type = "Intro", StartTicks = 100, EndTicks = 200 });

        var segments = await _provider.GetMediaSegments(Request(), CancellationToken.None);

        var segment = Assert.Single(segments);
        Assert.Equal(LocalId, segment.ItemId);
        Assert.Equal(MediaSegmentType.Intro, segment.Type);
        Assert.Equal(100, segment.StartTicks);
        Assert.Equal(200, segment.EndTicks);
        Assert.NotEqual(Guid.Empty, segment.Id);
    }

    /// <summary>
    /// When no server has segments for the file, its segments stay unless Remove Segments No Server Has is on.
    /// True: a pool with no intro tool never clears this server's segments, and one that asks for removal gets it.
    /// False: turning the provider on would wipe every segment here, or a removed segment would never go.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMediaSegments_NoServerHasAny_KeepsUnlessRemoving(bool remove)
    {
        _config.Configuration.MetadataSyncRemoveSegments = remove;
        Keep(Size, RunTime);

        var segments = await _provider.GetMediaSegments(Request(), CancellationToken.None);

        Assert.Equal(remove ? 0 : 1, segments.Count);
    }

    /// <summary>
    /// When nothing is kept for the item, or the item is gone here, the segments Jellyfin holds are given back.
    /// True: Jellyfin sees no change and keeps them, before the first scan and after a source stops answering.
    /// False: a provider with nothing kept would clear every segment here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMediaSegments_NothingKept_GivesBackWhatJellyfinHolds(bool itemGone)
    {
        var existing = Existing();
        var request = new MediaSegmentGenerationRequest { ItemId = itemGone ? Guid.NewGuid() : LocalId, ExistingSegments = existing };

        var segments = await _provider.GetMediaSegments(request, CancellationToken.None);

        Assert.Equal(existing, segments);
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.GetMediaSegments(null!, CancellationToken.None));
    }

    /// <summary>
    /// Segments kept for a source file of another size or runtime are not used, and the ones the provider gave are removed.
    /// True: an intro marked in another cut or encode never skips the wrong part of this file.
    /// False: a different file at the same path would take segments that do not fit it.
    /// </summary>
    [Theory]
    [InlineData(Size + 1, RunTime)]
    [InlineData(Size, RunTime + (20 * TimeSpan.TicksPerSecond))]
    public async Task GetMediaSegments_DifferentFile_GivesNone(long sourceSize, long sourceRunTime)
    {
        Keep(sourceSize, sourceRunTime, new PeerSegment { Type = "Intro", StartTicks = 1, EndTicks = 2 });

        Assert.Empty(await _provider.GetMediaSegments(Request(), CancellationToken.None));
    }

    /// <summary>
    /// Files match when nothing known says they differ: an unknown size or runtime is not a difference, nor is a link's size, nor runtimes within a second.
    /// True: a link, whose .strm is small, still takes its source's segments.
    /// False: every link would lose its segments, or a rounding difference in runtime would.
    /// </summary>
    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(1L, null, true, true)]
    [InlineData(Size, RunTime + 5000000L, false, true)]
    [InlineData(1L, RunTime, false, false)]
    public void SameFile_KnownValues_Decide(long? sourceSize, long? sourceRunTime, bool link, bool same)
    {
        var item = new Movie { Path = link ? "/local/movies/film.strm" : "/local/movies/film.mkv", RunTimeTicks = RunTime, Size = Size, IsShortcut = link };
        var held = new SourceMediaSegments("x", "a", "y", Array.Empty<PeerSegment>(), sourceSize, sourceRunTime, DateTime.UtcNow);

        Assert.Equal(same, PeerSegmentProvider.SameFile(item, held));
    }

    /// <summary>
    /// A segment is cut at the local file's end, left out when it starts there or later, and moved to zero when it starts before it.
    /// True: a segment never points past the end of the file it is stored for.
    /// False: Jellyfin would refuse the segment, or skip to a time the file does not have.
    /// </summary>
    [Theory]
    [InlineData(10L, 50L, 100L, 10L, 50L)]
    [InlineData(10L, 150L, 100L, 10L, 100L)]
    [InlineData(100L, 150L, 100L, null, null)]
    [InlineData(-5L, 50L, 100L, 0L, 50L)]
    [InlineData(10L, 150L, null, 10L, 150L)]
    [InlineData(50L, 50L, 100L, null, null)]
    public void Fit_Times_AreCutToTheLocalFile(long start, long end, long? runTime, long? fittedStart, long? fittedEnd)
    {
        var fitted = PeerSegmentProvider.Fit(new[] { new PeerSegment { Type = "Outro", StartTicks = start, EndTicks = end } }, LocalId, runTime);

        Assert.Equal(fittedStart, fitted.SingleOrDefault()?.StartTicks);
        Assert.Equal(fittedEnd, fitted.SingleOrDefault()?.EndTicks);
    }

    /// <summary>
    /// A segment whose type Jellyfin does not know is left out, and the rest keep their type.
    /// True: a source on a newer Jellyfin with a new segment type does not break the scan here.
    /// False: Jellyfin would store a segment of the wrong type.
    /// </summary>
    [Fact]
    public void Fit_UnknownTypes_AreLeftOut()
    {
        var fitted = PeerSegmentProvider.Fit(
            new PeerSegment?[]
            {
                null,
                new() { Type = "Sponsor", StartTicks = 1, EndTicks = 2 },
                new() { Type = "99", StartTicks = 1, EndTicks = 2 },
                new() { Type = "commercial", StartTicks = 1, EndTicks = 2 }
            },
            LocalId,
            100);

        Assert.Equal(MediaSegmentType.Commercial, Assert.Single(fitted).Type);
        Assert.Throws<ArgumentNullException>(() => PeerSegmentProvider.Fit(null!, LocalId, 100));
    }

    /// <summary>
    /// A segment two providers on the source both marked is given once, and segments that differ in type or time are all kept.
    /// True: a file whose intro two tools found shows one intro here.
    /// False: the same intro would be stored twice, and players would show it twice.
    /// </summary>
    [Fact]
    public void Fit_RepeatedSegment_IsGivenOnce()
    {
        var fitted = PeerSegmentProvider.Fit(
            new PeerSegment[]
            {
                new() { Type = "Intro", StartTicks = 10, EndTicks = 20 },
                new() { Type = "Intro", StartTicks = 10, EndTicks = 20 },
                new() { Type = "Intro", StartTicks = 10, EndTicks = 21 },
                new() { Type = "Recap", StartTicks = 10, EndTicks = 20 }
            },
            LocalId,
            100);

        Assert.Equal(3, fitted.Count);
        Assert.Equal(3, fitted.Select(f => f.Id).Distinct().Count());
    }

    /// <summary>
    /// With both switches on, the segments other providers gave a file are replaced by the source's of the same type,
    /// and kept when no server has any, since a pool that has not seen a server's own segments yet must never wipe them.
    /// True: the file keeps only the source's version, with no second intro from a local tool, and a ring never wipes what its maker just made.
    /// False: a local provider's intro would sit beside the source's, or a server's new intro would be wiped before it went out.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetMediaSegments_SourceSpeaks_RemovesOtherProvidersSegments(bool sourceHasSegments)
    {
        _config.Configuration.MetadataSyncReplaceSegments = true;
        _config.Configuration.MetadataSyncRemoveSegments = true;
        var other = new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = LocalId, Type = MediaSegmentType.Intro, StartTicks = 3, EndTicks = 4 };
        _others = new List<MediaSegmentDto> { other };
        if (sourceHasSegments)
        {
            Keep(Size, RunTime, new PeerSegment { Type = "Intro", StartTicks = 100, EndTicks = 200 });
        }
        else
        {
            Keep(Size, RunTime);
        }

        var segments = await _provider.GetMediaSegments(Request(), CancellationToken.None);

        Assert.Equal(sourceHasSegments ? 1 : 0, segments.Count);
        if (sourceHasSegments)
        {
            Assert.Equal(other.Id, Assert.Single(_deleted));
        }
        else
        {
            Assert.Empty(_deleted);
        }

        Assert.Equal(new[] { "Off", "Server Sync" }, Assert.Single(_askedWith).DisabledMediaSegmentProviders);
    }

    /// <summary>
    /// With nothing kept for the file, or a source file that differs, other providers' segments are left alone.
    /// True: a local tool's segments stay on every file no source speaks for.
    /// False: turning the provider on would wipe local segments across the library.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMediaSegments_SourceDoesNotSpeak_LeavesOtherProviders(bool differentFile)
    {
        _others = new List<MediaSegmentDto> { new() { Id = Guid.NewGuid(), ItemId = LocalId, Type = MediaSegmentType.Intro, StartTicks = 3, EndTicks = 4 } };
        if (differentFile)
        {
            Keep(Size + 1, RunTime, new PeerSegment { Type = "Intro", StartTicks = 100, EndTicks = 200 });
        }

        await _provider.GetMediaSegments(Request(), CancellationToken.None);

        Assert.Empty(_deleted);
        Assert.Empty(_askedWith);
    }

    /// <summary>
    /// When other providers already hold exactly the source's segments, nothing is removed and nothing is added.
    /// True: a file whose local intro matches the source's is left alone on every run.
    /// False: each run would delete the local intro and add the same one again.
    /// </summary>
    [Fact]
    public async Task GetMediaSegments_OthersMatchTheSource_ReplacesNothing()
    {
        _others = new List<MediaSegmentDto> { new() { Id = Guid.NewGuid(), ItemId = LocalId, Type = MediaSegmentType.Intro, StartTicks = 100, EndTicks = 200 } };
        Keep(Size, RunTime, new PeerSegment { Type = "Intro", StartTicks = 100, EndTicks = 200 });

        var segments = await _provider.GetMediaSegments(Request(), CancellationToken.None);

        Assert.Empty(segments);
        Assert.Empty(_deleted);
    }

    /// <summary>
    /// With both switches off, segments another provider gave a file stay and nothing is added beside them.
    /// True: a local intro tool's segments are never replaced or doubled unless asked for.
    /// False: turning the provider on would replace every local segment, or show two intros.
    /// </summary>
    [Fact]
    public async Task GetMediaSegments_OthersExistAndReplaceOff_KeepsThemAndAddsNothing()
    {
        _others = new List<MediaSegmentDto> { new() { Id = Guid.NewGuid(), ItemId = LocalId, Type = MediaSegmentType.Intro, StartTicks = 3, EndTicks = 4 } };
        Keep(Size, RunTime, new PeerSegment { Type = "Intro", StartTicks = 100, EndTicks = 200 });

        var segments = await _provider.GetMediaSegments(Request(), CancellationToken.None);

        Assert.Empty(segments);
        Assert.Empty(_deleted);
    }

    /// <summary>
    /// The plan for each case, type by type: the pool's segments go on a file with none of their type, stay off a type another provider covers
    /// unless replacing, never remove another provider's segments of a type the pool lacks, and with nothing in the pool the file keeps
    /// Server Sync's segments unless removing.
    /// True: the two switches decide exactly what changes, and a server's own segments are never wiped by a pool that has not seen them.
    /// False: a switch would remove or replace segments while off, or a ring would wipe the segments its maker just made.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false, false, 1, 0)]
    [InlineData(true, true, false, false, false, 1, 0)]
    [InlineData(true, true, false, true, false, 1, 0)]
    [InlineData(true, true, true, false, false, 0, 0)]
    [InlineData(true, true, true, true, false, 1, 1)]
    [InlineData(false, true, false, false, false, 2, 0)]
    [InlineData(false, true, false, false, true, 0, 0)]
    [InlineData(false, true, true, true, true, 0, 0)]
    [InlineData(false, false, false, false, true, 0, 0)]
    public void Plan_Cases_FollowTheSwitches(bool sourceHasAny, bool othersExist, bool sameType, bool replace, bool remove, int ours, int removed)
    {
        var fitted = sourceHasAny ? new List<MediaSegmentDto> { new() { Type = MediaSegmentType.Intro, StartTicks = 1, EndTicks = 2 } } : new List<MediaSegmentDto>();
        var others = othersExist ? new List<MediaSegmentDto> { new() { Type = sameType ? MediaSegmentType.Intro : MediaSegmentType.Outro, StartTicks = 5, EndTicks = 6 } } : new List<MediaSegmentDto>();
        var existing = new List<MediaSegmentDto> { new() { Type = MediaSegmentType.Recap, StartTicks = 1, EndTicks = 2 }, new() { Type = MediaSegmentType.Preview, StartTicks = 3, EndTicks = 4 } };

        var plan = PeerSegmentProvider.Plan(fitted, others, existing, replace, remove);

        Assert.Equal(ours, plan.Ours.Count);
        Assert.Equal(removed, plan.Remove.Count);
    }

    /// <summary>
    /// Two lists match when they hold the same segments by type and times, in any order, and not when one holds more.
    /// True: a match is found whatever order the providers listed them in.
    /// False: the same segments in another order would be replaced on every run.
    /// </summary>
    [Fact]
    public void SameSegments_OrderDoesNotMatter()
    {
        var a = new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = 1, EndTicks = 2 };
        var b = new MediaSegmentDto { Type = MediaSegmentType.Outro, StartTicks = 5, EndTicks = 9 };

        Assert.True(PeerSegmentProvider.SameSegments(new[] { a, b }, new[] { b, a }));
        Assert.False(PeerSegmentProvider.SameSegments(new[] { a }, new[] { a, b }));
    }

    private void Keep(long size, long runTime, params PeerSegment[] segments)
        => _store.Save(new[] { new SourceMediaSegments(LocalId.ToString("N"), "a", "source-item", segments, size, runTime, DateTime.UtcNow) });

    private static MediaSegmentGenerationRequest Request() => new() { ItemId = LocalId, ExistingSegments = Existing() };

    private static List<MediaSegmentDto> Existing()
        => new() { new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = LocalId, Type = MediaSegmentType.Intro, StartTicks = 1, EndTicks = 2 } };
}
