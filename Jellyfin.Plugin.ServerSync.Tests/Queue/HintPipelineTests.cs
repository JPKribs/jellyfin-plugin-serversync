using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public sealed class HintPipelineTests : IDisposable
{
    private const string OriginId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";
    private static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid LocalUserId = Guid.Parse("33333333333333333333333333333333");

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly VersionStore _versions;
    private readonly InboundHintStore _inbound;
    private readonly OutboundHintStore _outbound;
    private readonly FakeLocalItems _local = new();
    private readonly FakeConfigManager _config = new();

    public HintPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        var provider = new FixedProvider(_database);
        _versions = new VersionStore(provider, NullLogger<VersionStore>.Instance);
        _inbound = new InboundHintStore(provider, NullLogger<InboundHintStore>.Instance);
        _outbound = new OutboundHintStore(provider, NullLogger<OutboundHintStore>.Instance);
        _config.Configuration.EnableMetadataSync = true;
        _config.Configuration.EnableContentSync = true;
        _config.Configuration.EnableHistorySync = true;
        _config.Configuration.EnablePeopleSync = true;
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Triage_TurnsAwayWhatThisServerCannotUse()
    {
        var triage = Triage();
        var origin = Origin();

        Assert.Equal(HintCode.NotMapped, triage.Assess(origin, Hint(HintKind.Metadata, "/elsewhere/a.mkv"), OriginId, DateTime.UtcNow).Code);
        Assert.Equal(HintCode.Invalid, triage.Assess(origin, Hint(HintKind.Metadata, "/media/movies/a.mkv"), "someone-else", DateTime.UtcNow).Code);
        Assert.Equal(HintCode.FutureVersion, triage.Assess(origin, Hint(HintKind.Metadata, "/media/movies/a.mkv", DateTime.UtcNow.AddHours(1)), OriginId, DateTime.UtcNow).Code);
        Assert.Equal(HintCode.Unsupported, triage.Assess(origin, Hint(HintKind.Users, null, key: "u"), OriginId, DateTime.UtcNow).Code);

        _config.Configuration.EnableMetadataSync = false;
        Assert.Equal(HintCode.ModuleOff, triage.Assess(origin, Hint(HintKind.Metadata, "/media/movies/a.mkv"), OriginId, DateTime.UtcNow).Code);
    }

    [Fact]
    public void Triage_Playlists_NeedTheModuleAndAMappedOwner()
    {
        var triage = Triage();
        var origin = Origin();
        var hint = PlaylistHint(UserId);

        Assert.Equal(HintCode.ModuleOff, triage.Assess(origin, hint, OriginId, DateTime.UtcNow).Code);

        _config.Configuration.EnablePlaylistSync = true;
        Assert.Equal(HintCode.Queued, triage.Assess(origin, hint, OriginId, DateTime.UtcNow).Code);
        Assert.Equal(HintCode.NotMapped, triage.Assess(origin, PlaylistHint(Guid.NewGuid()), OriginId, DateTime.UtcNow).Code);
    }

    [Fact]
    public void Triage_Playlists_PullOriginAlwaysWins_SyncOriginDecidesByVersion()
    {
        _config.Configuration.EnablePlaylistSync = true;
        var triage = Triage();
        var hint = PlaylistHint(UserId, DateTime.UtcNow.AddMinutes(-10));
        _versions.Set(new ObjectVersion { Kind = HintKind.Playlists, Key = hint.Key, ServerId = "local", Timestamp = DateTime.UtcNow.AddMinutes(-1) });

        var pull = Origin();
        Assert.Equal(HintCode.Queued, triage.Assess(pull, hint, OriginId, DateTime.UtcNow).Code);

        var sync = Origin();
        sync.Mode = ServerMode.Sync;
        Assert.Equal(HintCode.Superseded, triage.Assess(sync, hint, OriginId, DateTime.UtcNow).Code);
    }

    [Fact]
    public void Triage_HonorsTheMappingFilter()
    {
        var origin = Origin();
        origin.LibraryMappings[0].FilterMode = LibraryFilterMode.Blacklist;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });

        var assessment = Triage().Assess(origin, Hint(HintKind.Content, "/media/movies/Hidden/film.mkv"), OriginId, DateTime.UtcNow);

        Assert.Equal(HintCode.Filtered, assessment.Code);
        Assert.False(assessment.Keep);
    }

    [Fact]
    public void Triage_ParksAHintWhoseLocalItemHasNotArrived()
    {
        var assessment = Triage().Assess(Origin(), Hint(HintKind.Metadata, "/media/movies/a.mkv"), OriginId, DateTime.UtcNow);

        Assert.Equal(HintCode.NoLocalItem, assessment.Code);
        Assert.True(assessment.Keep);
        Assert.Equal(HintTriage.PathParkKey(LocalPath("a.mkv")), assessment.ParkKey);
    }

    [Fact]
    public void Triage_QueuesContentWithoutLookingForALocalItem()
    {
        Assert.Equal(HintCode.Queued, Triage().Assess(Origin(), Hint(HintKind.Content, "/media/movies/a.mkv"), OriginId, DateTime.UtcNow).Code);
    }

    [Fact]
    public void Triage_SkipsAVersionThisServerAlreadyHoldsOrBeat()
    {
        var local = _local.Add(LocalPath("a.mkv"));
        var at = DateTime.UtcNow.AddMinutes(-10);
        var triage = Triage();

        Assert.Equal(HintCode.Queued, triage.Assess(Origin(), Hint(HintKind.Metadata, "/media/movies/a.mkv", at), OriginId, DateTime.UtcNow).Code);

        _versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = HintProtocol.MetadataKey(local.Id), ServerId = OriginId, Timestamp = at });
        Assert.Equal(HintCode.AlreadyCurrent, triage.Assess(Origin(), Hint(HintKind.Metadata, "/media/movies/a.mkv", at), OriginId, DateTime.UtcNow).Code);

        _versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = HintProtocol.MetadataKey(local.Id), ServerId = "local", Timestamp = at.AddMinutes(5) });
        Assert.Equal(HintCode.Superseded, triage.Assess(Origin(), Hint(HintKind.Metadata, "/media/movies/a.mkv", at), OriginId, DateTime.UtcNow).Code);
        Assert.Equal(HintCode.Queued, triage.Assess(Origin(), Hint(HintKind.Metadata, "/media/movies/a.mkv", at.AddMinutes(6)), OriginId, DateTime.UtcNow).Code);
    }

    [Fact]
    public void Triage_ChecksHistoryUsersAndPeople()
    {
        var triage = Triage();
        var unmapped = Hint(HintKind.History, "/media/movies/a.mkv", key: HintProtocol.HistoryKey(Guid.NewGuid(), ItemId));
        Assert.Equal(HintCode.NotMapped, triage.Assess(Origin(), unmapped, OriginId, DateTime.UtcNow).Code);

        var mapped = Hint(HintKind.History, "/media/movies/a.mkv", key: HintProtocol.HistoryKey(UserId, ItemId));
        Assert.Equal(HintCode.NoLocalItem, triage.Assess(Origin(), mapped, OriginId, DateTime.UtcNow).Code);

        var person = triage.Assess(Origin(), Hint(HintKind.People, null, key: "Jane Doe"), OriginId, DateTime.UtcNow);
        Assert.Equal(HintCode.NoLocalItem, person.Code);
        Assert.Equal(HintTriage.PersonParkKey("jane doe"), person.ParkKey);
    }

    [Fact]
    public void Interest_DescribesWhatThisServerTakesFromAnOrigin()
    {
        var origin = Origin();
        _config.Configuration.EnableContentSync = false;

        var interest = PeerInterest.For(_config.Configuration, origin);

        Assert.DoesNotContain(HintKind.Content, interest.Kinds);
        Assert.True(interest.Wants(HintKind.Metadata, "/media/movies/a.mkv", Guid.Empty));
        Assert.False(interest.Wants(HintKind.Metadata, "/media/shows/a.mkv", Guid.Empty));
        Assert.False(interest.Wants(HintKind.Content, "/media/movies/a.mkv", Guid.Empty));
        Assert.True(interest.Wants(HintKind.History, "/media/movies/a.mkv", UserId));
        Assert.False(interest.Wants(HintKind.History, "/media/movies/a.mkv", Guid.NewGuid()));
        Assert.True(interest.Wants(HintKind.People, null, Guid.Empty));
    }

    [Fact]
    public void Interest_LeavesOutFilteredPaths_AndRevisesWhenMappingsChange()
    {
        var origin = Origin();
        var before = PeerInterest.For(_config.Configuration, origin);
        origin.LibraryMappings[0].FilterMode = LibraryFilterMode.Whitelist;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Wanted" });

        var after = PeerInterest.For(_config.Configuration, origin);

        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(after.Revision, PeerInterest.For(_config.Configuration, origin).Revision);
        Assert.True(after.Wants(HintKind.Metadata, "/media/movies/Wanted/film.mkv", Guid.Empty));
        Assert.False(after.Wants(HintKind.Metadata, "/media/movies/Other/film.mkv", Guid.Empty));
    }

    [Theory]
    [InlineData(HintCode.NotMapped, true)]
    [InlineData(HintCode.Filtered, true)]
    [InlineData(HintCode.Ignored, true)]
    [InlineData(HintCode.ModuleOff, true)]
    [InlineData(HintCode.NoLocalItem, false)]
    [InlineData(HintCode.Locked, false)]
    [InlineData(HintCode.Applied, false)]
    [InlineData(HintCode.Superseded, false)]
    [InlineData(HintCode.SourceGone, false)]
    [InlineData(HintCode.GaveUp, false)]
    public void Suppression_HoldsBackOnlyCodesThatWillRepeat(HintCode code, bool held)
    {
        var suppression = new HintSuppression();
        var now = DateTime.UtcNow;

        suppression.Note("peer", HintKind.Metadata, "key", code, "rev", now);

        Assert.Equal(held, suppression.IsHeld("peer", HintKind.Metadata, "key", "rev", now));
    }

    [Fact]
    public void Suppression_EndsWithTimeOrANewRevision()
    {
        var suppression = new HintSuppression();
        var now = DateTime.UtcNow;
        suppression.Note("peer", HintKind.People, "Jane Doe", HintCode.NotMapped, "rev", now);

        Assert.True(suppression.IsHeld("peer", HintKind.People, "JANE DOE", "rev", now));
        Assert.False(suppression.IsHeld("other", HintKind.People, "Jane Doe", "rev", now));
        Assert.False(suppression.IsHeld("peer", HintKind.People, "Jane Doe", "rev", now + HintSuppression.Hold));

        suppression.Note("peer", HintKind.People, "Jane Doe", HintCode.NotMapped, "rev", now);
        Assert.False(suppression.IsHeld("peer", HintKind.People, "Jane Doe", "rev2", now));

        suppression.Note("peer", HintKind.Metadata, "a", HintCode.NotMapped, null, now);
        Assert.Equal(1, suppression.ClearPeer("peer"));
        Assert.False(suppression.IsHeld("peer", HintKind.Metadata, "a", null, now));
    }

    [Fact]
    public void ParkedHint_WaitsUntilItsItemArrives()
    {
        var now = DateTime.UtcNow;
        var row = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/a.mkv"), now);
        row.ParkedPath = HintTriage.PathParkKey(LocalPath("a.mkv"));
        row.NextAttempt = now + HintProtocol.ParkWindow;
        _inbound.Enqueue(row);

        Assert.Empty(_inbound.GetDue(now.AddMinutes(1), 10, content: false));
        Assert.Equal(HintTriage.PathParkKey(LocalPath("a.mkv")), _inbound.GetAll().Single().ParkedPath);

        Assert.Equal(0, _inbound.WakeParked(HintTriage.PathParkKey(LocalPath("b.mkv")), now));
        Assert.Equal(1, _inbound.WakeParked(HintTriage.PathParkKey(LocalPath("a.mkv")).ToUpperInvariant(), now));

        var due = Assert.Single(_inbound.GetDue(now.AddSeconds(1), 10, content: false));
        Assert.Null(due.ParkedPath);
    }

    [Fact]
    public void ParkWindow_IsSixHours()
    {
        Assert.Equal(TimeSpan.FromHours(6), HintProtocol.ParkWindow);
    }

    [Fact]
    public void ShortenParked_MakesRowsFromALongerWindowDue_AndLeavesTheRestAlone()
    {
        var now = DateTime.UtcNow;
        var old = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/old.mkv", key: Guid.NewGuid().ToString("N"), id: "old"), now.AddDays(-1));
        old.ParkedPath = HintTriage.PathParkKey(LocalPath("old.mkv"));
        old.NextAttempt = now.AddDays(6);
        _inbound.Enqueue(old);

        var current = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/current.mkv", key: Guid.NewGuid().ToString("N"), id: "current"), now);
        current.ParkedPath = HintTriage.PathParkKey(LocalPath("current.mkv"));
        current.NextAttempt = now + HintProtocol.ParkWindow;
        _inbound.Enqueue(current);

        var retrying = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/retry.mkv", key: Guid.NewGuid().ToString("N"), id: "retry"), now);
        retrying.NextAttempt = now.AddDays(2);
        _inbound.Enqueue(retrying);

        Assert.Equal(1, _inbound.ShortenParked(now, HintProtocol.ParkWindow));

        var due = Assert.Single(_inbound.GetDue(now.AddSeconds(1), 10, content: false));
        Assert.Equal(OriginId + ":old", due.HintId);
        Assert.NotNull(due.ParkedPath);
        Assert.Equal(0, _inbound.ShortenParked(now, HintProtocol.ParkWindow));
    }

    [Fact]
    public void ParkedRows_DoNotCountTowardTheCap_AndKeepTheirOwnLimit()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            var parked = InboundHint.FromHint(Hint(HintKind.Metadata, $"/media/movies/p{i}.mkv", key: Guid.NewGuid().ToString("N"), id: "p" + i), now.AddMinutes(i));
            parked.ParkedPath = HintTriage.PathParkKey(LocalPath($"p{i}.mkv"));
            parked.NextAttempt = now + HintProtocol.ParkWindow;
            _inbound.Enqueue(parked);
        }

        _inbound.Enqueue(InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/live.mkv", key: Guid.NewGuid().ToString("N"), id: "live"), now));

        Assert.Equal(1, _inbound.CountForOrigin(OriginId));
        Assert.Equal(1, _inbound.TrimParked(OriginId, 2));
        Assert.DoesNotContain(_inbound.GetAll(), r => r.HintId == OriginId + ":p0");
        Assert.Equal(3, _inbound.GetAll().Count);
    }

    [Fact]
    public void OlderDuplicate_DoesNotOverwriteTheNewerRow()
    {
        var now = DateTime.UtcNow;
        var newer = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/renamed.mkv", now.AddMinutes(-1)), now);
        _inbound.Enqueue(newer);
        var older = InboundHint.FromHint(Hint(HintKind.Metadata, "/media/movies/old.mkv", now.AddMinutes(-10)), now);
        older.ParkedPath = HintTriage.PathParkKey(LocalPath("old.mkv"));
        older.NextAttempt = now + HintProtocol.ParkWindow;
        _inbound.Enqueue(older);

        var row = Assert.Single(_inbound.GetAll());
        Assert.Equal("/media/movies/renamed.mkv", row.ItemPath);
        Assert.Null(row.ParkedPath);
        Assert.Single(_inbound.GetDue(now.AddSeconds(1), 10, content: false));
    }

    [Fact]
    public void Present_ChecksThePathOrThePerson()
    {
        _local.Add(LocalPath("here.mkv"));
        var triage = Triage();

        Assert.True(triage.Present(HintTriage.PathParkKey(LocalPath("here.mkv")), null));
        Assert.False(triage.Present(HintTriage.PathParkKey(LocalPath("gone.mkv")), null));
        Assert.False(triage.Present(HintTriage.PersonParkKey("Jane Doe"), "Jane Doe"));
    }

    [Fact]
    public void Park_PutsADueRowAside()
    {
        var now = DateTime.UtcNow;
        var row = InboundHint.FromHint(Hint(HintKind.People, null, key: "Jane Doe"), now);
        _inbound.Enqueue(row);

        _inbound.Park(row, HintTriage.PersonParkKey("Jane Doe"), now + HintProtocol.ParkWindow, "waiting");

        Assert.Empty(_inbound.GetDue(now.AddMinutes(1), 10, content: false));
        Assert.Equal(1, _inbound.WakeParked(HintTriage.PersonParkKey("jane doe"), now));
    }

    [Fact]
    public void Queue_AnswersEachHintWithItsCode_AndParksWhatWaits()
    {
        var origin = Origin();
        origin.Mode = ServerMode.Sync;
        _config.Configuration.Servers.Add(origin);
        _local.Add(LocalPath("here.mkv"));
        var pairings = new PeerPairingStore(new FixedProvider(_database), new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);
        pairings.SetInbound(origin.Key, "issued");
        var controller = Controller(pairings, "issued");
        var request = new QueueRequest
        {
            SenderServerId = OriginId,
            Items =
            {
                Hint(HintKind.Metadata, "/elsewhere/x.mkv", id: "1"),
                Hint(HintKind.Metadata, "/media/movies/missing.mkv", id: "2"),
                Hint(HintKind.Metadata, "/media/movies/here.mkv", id: "3", key: Guid.NewGuid().ToString("N"))
            }
        };

        var answer = Assert.IsType<OkObjectResult>(controller.Queue(request).Result).Value as QueueResponse;

        Assert.Equal(new[] { HintCode.NotMapped, HintCode.NoLocalItem, HintCode.Queued }, answer!.Items.Select(i => i.Code).ToArray());
        var stored = _inbound.GetAll();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, r => r.ParkedPath == HintTriage.PathParkKey(LocalPath("missing.mkv")) && r.NextAttempt > DateTime.UtcNow.AddHours(5) && r.NextAttempt <= DateTime.UtcNow + HintProtocol.ParkWindow);
        Assert.Contains(stored, r => r.ParkedPath is null);
    }

    [Fact]
    public void Completion_WithACodeThatRepeats_StopsTheSenderSendingThatObject()
    {
        var peer = Origin();
        var worker = new OutboundHintWorker(_outbound, _config, null!, null!, null!, new HintSuppression(), null!, NullLogger<OutboundHintWorker>.Instance);
        _outbound.Enqueue(new OutboundHint { PeerKey = peer.Key, Kind = HintKind.Metadata, Key = "obj", ItemId = "obj", VersionServerId = "me", VersionTimestamp = DateTime.UtcNow.AddMinutes(-1) }, "me");
        _outbound.Enqueue(new OutboundHint { PeerKey = peer.Key, Kind = HintKind.Metadata, Key = "fine", ItemId = "fine", VersionServerId = "me", VersionTimestamp = DateTime.UtcNow.AddMinutes(-1) }, "me");
        var rows = _outbound.GetAll().ToDictionary(r => r.Key);

        var removed = worker.Complete(peer, new List<CompletedHint>
        {
            new() { HintId = rows["obj"].HintId, VersionTimestamp = DateTime.UtcNow, Code = HintCode.NotMapped },
            new() { HintId = rows["fine"].HintId, VersionTimestamp = DateTime.UtcNow, Code = HintCode.Applied }
        });

        Assert.Equal(2, removed);
        Assert.False(worker.PeerWants(peer.Key, HintKind.Metadata, "/media/movies/a.mkv", Guid.Empty, "obj"));
        Assert.True(worker.PeerWants(peer.Key, HintKind.Metadata, "/media/movies/a.mkv", Guid.Empty, "fine"));
    }

    [Fact]
    public void FieldWriter_FillsANewItemFromABlob()
    {
        var movie = new Movie();
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Name"] = "Film",
            ["Overview"] = "About it",
            ["ProductionYear"] = 2001,
            ["Genres"] = new[] { "Drama" },
            ["ProviderIds"] = new Dictionary<string, string> { ["Tmdb"] = "42" },
            ["LockData"] = true
        });

        Assert.True(MetadataFieldWriter.Apply(movie, json, syncGenres: true, syncTags: true, syncLocks: false));

        Assert.Equal("Film", movie.Name);
        Assert.Equal("About it", movie.Overview);
        Assert.Equal(2001, movie.ProductionYear);
        Assert.Equal(new[] { "Drama" }, movie.Genres);
        Assert.Equal("42", movie.ProviderIds["Tmdb"]);
        Assert.False(movie.IsLocked);
    }

    [Fact]
    public void FieldWriter_ReadsPeopleInBillingOrder_AndStudios()
    {
        var people = MetadataFieldWriter.ReadPeople("[{\"Name\":\"B\",\"Type\":\"Actor\",\"Role\":\"Lead\"},{\"Name\":\"A\",\"Type\":\"Director\"},{\"Role\":\"nameless\"}]");

        Assert.Equal(new[] { "B", "A" }, people.Select(p => p.Name).ToArray());
        Assert.Equal(new int?[] { 0, 1 }, people.Select(p => p.SortOrder).ToArray());
        Assert.Equal("Lead", people[0].Role);
        Assert.Equal(new[] { "S1", "S2" }, MetadataFieldWriter.ReadStudios("[\"S1\",\"S2\"]"));
        Assert.Empty(MetadataFieldWriter.ReadStudios(null));
    }

    [Theory]
    [InlineData("/media/movies", "/local/movies", "/local/movies/Film (2001)/film.mkv", "/media/movies/Film (2001)/film.mkv")]
    [InlineData("D:\\Media\\Movies", "/local/movies", "/local/movies/Film/film.mkv", "D:\\Media\\Movies\\Film\\film.mkv")]
    [InlineData("/media/movies", "/local/movies", "/local/movies", "/media/movies")]
    [InlineData("/media/movies", "/local/movies", "/local/shows/x.mkv", null)]
    public void Provider_TranslatesALocalPathToTheParents(string sourceRoot, string localRoot, string localPath, string? expected)
    {
        var parent = new SourceServer { Key = "p", LibraryMappings = { new LibraryMapping { SourceLibraryId = "s", SourceRootPath = sourceRoot, LocalLibraryId = "l", LocalRootPath = localRoot } } };

        Assert.Equal(expected, PeerMetadataSource.ToParentPath(parent, localPath));
    }

    [Fact]
    public void Provider_LooksUpPeopleByName_AndSkipsUnmappedItems()
    {
        var parent = Origin();

        var person = PeerMetadataSource.LookupFor(parent, Keys(nameof(Person), null, "Jane Doe"));
        Assert.NotNull(person);
        Assert.Equal("Jane Doe", person!.Name);
        Assert.Null(person.Path);

        Assert.Null(PeerMetadataSource.LookupFor(parent, Keys(nameof(Movie), "/somewhere/else.mkv", "Film")));

        var movie = PeerMetadataSource.LookupFor(parent, Keys(nameof(Movie), LocalPath("film.mkv"), "Film"));
        Assert.Equal("/media/movies/film.mkv", movie!.Path);
        Assert.Null(movie.Name);
    }

    [Fact]
    public void Provider_FallsBackToIds_WhenThePathIsNotMapped()
    {
        var parent = Origin();
        var ids = new Dictionary<string, string> { ["Tvdb"] = "446831", ["Empty"] = "" };

        var movie = PeerMetadataSource.LookupFor(parent, Keys(nameof(Movie), "/somewhere/else.mkv", "Film", ids));

        Assert.NotNull(movie);
        Assert.Null(movie!.Path);
        Assert.Equal(new[] { "Tvdb" }, movie.ProviderIds!.Keys.ToArray());
    }

    [Fact]
    public void Provider_MatchesAnEpisodeBySeriesIdsAndNumbers()
    {
        var parent = Origin();
        var series = new Dictionary<string, string> { ["Tvdb"] = "446831" };

        var episode = PeerMetadataSource.LookupFor(parent, Keys("Episode", "/somewhere/else/S02E05.mkv", "Episode 5", null, series, 5, 2));
        Assert.NotNull(episode);
        Assert.Equal("446831", episode!.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(5, episode.IndexNumber);
        Assert.Equal(2, episode.ParentIndexNumber);

        Assert.Null(PeerMetadataSource.LookupFor(parent, Keys("Episode", "/somewhere/else/S02E05.mkv", "Episode 5", null, series, null, 2)));
    }

    private static PeerItemKeys Keys(string type, string? path, string? name, Dictionary<string, string>? ids = null, Dictionary<string, string>? seriesIds = null, int? index = null, int? parentIndex = null)
        => new(type, path, name, ids, seriesIds, index, parentIndex);

    [Fact]
    public void ProviderFills_RememberPathsAndPeople()
    {
        ProviderFills.MarkPath("/local/movies/filled.mkv");
        ProviderFills.MarkPerson("Jane Doe");

        Assert.True(ProviderFills.WasFilled("/local/movies/filled.mkv", null, DateTime.UtcNow));
        Assert.True(ProviderFills.WasFilled(null, "JANE DOE", DateTime.UtcNow));
        Assert.False(ProviderFills.WasFilled("/local/movies/other.mkv", null, DateTime.UtcNow));
        Assert.False(ProviderFills.WasFilled("/local/movies/filled.mkv", null, DateTime.UtcNow + ProviderFills.Remembered));
    }

    private HintTriage Triage() => new(_config, _local, _versions);

    private PeerController Controller(PeerPairingStore pairings, string presented)
    {
        var inboundWorker = new InboundHintWorker(_inbound, _config, null!, null!, null!, null!, null!, null!, Triage(), null!, NullLogger<InboundHintWorker>.Instance);
        var controller = new PeerController(null!, _config, null!, _inbound, _outbound, _versions, inboundWorker, null!, pairings, null!, Triage(), null!, NullLogger<PeerController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers[HintProtocol.PairingHeader] = presented;
        return controller;
    }

    private static string LocalPath(string name) => PathUtilities.TranslatePath("/media/movies/" + name, "/media/movies", "/local/movies");

    private static SourceServer Origin() => new()
    {
        Key = "origin",
        Name = "Origin",
        Url = "http://origin:8096",
        ApiKey = "key",
        ServerId = OriginId,
        LibraryMappings =
        {
            new LibraryMapping { SourceLibraryId = "lib", SourceRootPath = "/media/movies", LocalLibraryId = "local", LocalRootPath = "/local/movies" }
        },
        UserMappings =
        {
            new UserMapping { SourceUserId = UserId.ToString("N"), LocalUserId = LocalUserId.ToString("N") }
        }
    };

    private static SyncHint Hint(HintKind kind, string? path, DateTime? at = null, string? key = null, string? id = null) => new()
    {
        HintId = OriginId + ":" + (id ?? "1"),
        OriginServerId = OriginId,
        Kind = kind,
        Key = key ?? ItemId.ToString("N"),
        ItemPath = path,
        VersionServerId = OriginId,
        VersionTimestamp = at ?? DateTime.UtcNow.AddMinutes(-1)
    };

    private static SyncHint PlaylistHint(Guid owner, DateTime? at = null) => new()
    {
        HintId = OriginId + ":p",
        OriginServerId = OriginId,
        Kind = HintKind.Playlists,
        Key = "44444444444444444444444444444444",
        ItemId = "55555555555555555555555555555555",
        ItemType = "Playlist",
        UserId = owner.ToString("N"),
        VersionServerId = OriginId,
        VersionTimestamp = at ?? DateTime.UtcNow.AddMinutes(-1)
    };

    private sealed class FakeLocalItems : ILocalItems
    {
        private readonly Dictionary<string, BaseItem> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public BaseItem Add(string path)
        {
            var item = new Movie { Id = Guid.NewGuid(), Path = path };
            _byPath[path] = item;
            return item;
        }

        public BaseItem? FindByPath(string path) => _byPath.TryGetValue(path, out var item) ? item : null;

        public Person? GetPerson(string name) => null;
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public PluginConfiguration Configuration { get; } = new();

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";
    }
}
