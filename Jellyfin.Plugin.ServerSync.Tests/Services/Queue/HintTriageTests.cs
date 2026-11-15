using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Triage answers each arriving hint with the one code that fits it, and stores nothing malformed, oversized, or unusable.
/// </summary>
public sealed class HintTriageTests : IDisposable
{
    private const string OriginId = QueueSamples.OriginId;
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly VersionStore _versions;
    private readonly QueueLocalItems _local = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly HintTriage _triage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintTriageTests"/> class.
    /// </summary>
    public HintTriageTests()
    {
        _versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        _triage = new HintTriage(_config, _local, _versions);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static SyncHint Hint(HintKind kind, string? path = "/media/movies/a.mkv", string? key = null, DateTime? at = null)
        => QueueSamples.Hint(kind, path, key, at ?? Now.AddMinutes(-1));

    private HintAssessment Assess(SyncHint hint, SourceServer? origin = null)
        => _triage.Assess(origin ?? QueueSamples.Server(), hint, OriginId, Now);

    /// <summary>
    /// A missing collaborator or argument is refused.
    /// True: a wiring mistake fails at startup and a bad call is named.
    /// False: a peer's request ends in a null reference.
    /// </summary>
    [Fact]
    public void ConstructorAndAssess_NullArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new HintTriage(null!, _local, _versions));
        Assert.Throws<ArgumentNullException>(() => new HintTriage(_config, null!, _versions));
        Assert.Throws<ArgumentNullException>(() => new HintTriage(_config, _local, null!));
        Assert.Throws<ArgumentNullException>(() => _triage.Assess(null!, Hint(HintKind.Content), OriginId, Now));
        Assert.Throws<ArgumentNullException>(() => _triage.Assess(QueueSamples.Server(), null!, OriginId, Now));
        Assert.Throws<ArgumentNullException>(() => _triage.Assess(QueueSamples.Server(), Hint(HintKind.Content), null!, Now));
    }

    /// <summary>
    /// A hint with any field longer than this server stores is invalid.
    /// True: a peer cannot fill the disk with one enormous hint.
    /// False: an unbounded field is written to the queue on arrival.
    /// </summary>
    [Theory]
    [InlineData("Key", HintTriage.MaxPathLength + 1)]
    [InlineData("ItemPath", HintTriage.MaxPathLength + 1)]
    [InlineData("ItemId", HintTriage.MaxIdLength + 1)]
    [InlineData("ItemType", HintTriage.MaxIdLength + 1)]
    [InlineData("UserId", HintTriage.MaxIdLength + 1)]
    [InlineData("UserName", HintTriage.MaxIdLength + 1)]
    [InlineData("OriginServerId", HintTriage.MaxIdLength + 1)]
    [InlineData("VersionServerId", HintTriage.MaxIdLength + 1)]
    public void Assess_FieldLongerThanStored_IsInvalid(string field, int length)
    {
        var hint = Hint(HintKind.Content);
        typeof(SyncHint).GetProperty(field)!.SetValue(hint, new string('x', length));

        var assessment = Assess(hint);

        Assert.Equal(HintCode.Invalid, assessment.Code);
        Assert.False(assessment.Keep);
    }

    /// <summary>
    /// A key of exactly the longest length stored is not turned away for its length.
    /// True: the longest person name this server can hold still syncs.
    /// False: the limit is off by one and a legal key is refused.
    /// </summary>
    [Fact]
    public void Assess_KeyAtTheLongestLength_IsNotInvalid()
    {
        var hint = Hint(HintKind.People, path: null, key: new string('n', HintTriage.MaxPathLength));

        var assessment = Assess(hint);

        Assert.Equal(HintCode.NoLocalItem, assessment.Code);
    }

    /// <summary>
    /// A hint with no key is invalid.
    /// True: a hint that names nothing is answered and not stored.
    /// False: a keyless row enters the queue and collides with every other keyless row.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Assess_NoKey_IsInvalid(string? key)
    {
        var hint = Hint(HintKind.Content);
        hint.Key = key!;

        var assessment = Assess(hint);

        Assert.Equal(HintCode.Invalid, assessment.Code);
    }

    /// <summary>
    /// The sender's id matches the hint's origin, version, and hint id in any case.
    /// True: a peer that spells its id in upper case is still recognised as itself.
    /// False: every hint from that peer is refused as forged.
    /// </summary>
    [Fact]
    public void Assess_SenderIdInAnotherCase_IsAccepted()
    {
        var hint = Hint(HintKind.Content);
        hint.OriginServerId = OriginId.ToUpperInvariant();
        hint.VersionServerId = OriginId.ToUpperInvariant();
        hint.HintId = OriginId.ToUpperInvariant() + ":1";

        var assessment = Assess(hint);

        Assert.Equal(HintCode.Queued, assessment.Code);
    }

    /// <summary>
    /// A hint with no hint id is invalid.
    /// True: every stored hint can be completed by id.
    /// False: a row with no id can never be reported done and is sent forever.
    /// </summary>
    [Fact]
    public void Assess_NoHintId_IsInvalid()
    {
        var hint = Hint(HintKind.Content);
        hint.HintId = null!;

        var assessment = Assess(hint);

        Assert.Equal(HintCode.Invalid, assessment.Code);
    }

    /// <summary>
    /// A stamp up to the allowed lead ahead of this clock is taken, and one past it is declined.
    /// True: honest clock skew syncs, and a broken clock is named to the sender.
    /// False: a few seconds of skew blocks a peer, or a far future stamp gets in.
    /// </summary>
    [Theory]
    [InlineData(0, HintCode.Queued)]
    [InlineData(1, HintCode.FutureVersion)]
    public void Assess_StampAtAndPastTheAllowedLead_IsTakenThenDeclined(long ticksPastTheLead, HintCode expected)
    {
        var hint = Hint(HintKind.Content, at: Now + HintProtocol.MaxVersionLead + TimeSpan.FromTicks(ticksPastTheLead));

        var assessment = Assess(hint);

        Assert.Equal(expected, assessment.Code);
    }

    /// <summary>
    /// A hint for a sync type that is off is declined as such, for every type.
    /// True: the sender learns to hold the type back until it is turned on.
    /// False: hints for a disabled type are stored and fail on apply.
    /// </summary>
    [Theory]
    [InlineData(HintKind.Content)]
    [InlineData(HintKind.History)]
    [InlineData(HintKind.People)]
    [InlineData(HintKind.Playlists)]
    public void Assess_SyncTypeOff_IsModuleOff(HintKind kind)
    {
        _config.Configuration.EnableContentSync = false;
        _config.Configuration.EnableHistorySync = false;
        _config.Configuration.EnablePeopleSync = false;
        _config.Configuration.EnablePlaylistSync = false;

        var assessment = Assess(Hint(kind));

        Assert.Equal(HintCode.ModuleOff, assessment.Code);
    }

    /// <summary>
    /// A kind this build does not know is unsupported.
    /// True: a newer peer's new hint type is declined cleanly.
    /// False: an unknown kind is queued and fails on every apply.
    /// </summary>
    [Fact]
    public void Assess_UnknownKind_IsUnsupported()
    {
        var assessment = Assess(Hint((HintKind)99));

        Assert.Equal(HintCode.Unsupported, assessment.Code);
    }

    /// <summary>
    /// A metadata or content hint whose key is not an item id is invalid.
    /// True: a malformed key is answered before any lookup.
    /// False: the apply later fails on a key it cannot parse.
    /// </summary>
    [Theory]
    [InlineData(HintKind.Metadata)]
    [InlineData(HintKind.Content)]
    public void Assess_ItemKeyNotAnId_IsInvalid(HintKind kind)
    {
        var assessment = Assess(Hint(kind, key: "not-an-id"));

        Assert.Equal(HintCode.Invalid, assessment.Code);
    }

    /// <summary>
    /// A history hint whose key is not a user id and an item id is invalid.
    /// True: a malformed history key never reaches the user mapping.
    /// False: half a key is matched against the wrong user.
    /// </summary>
    [Theory]
    [InlineData("not-a-key")]
    [InlineData("22222222222222222222222222222222|")]
    [InlineData("|11111111111111111111111111111111")]
    [InlineData("22222222222222222222222222222222|not-an-id")]
    public void Assess_HistoryKeyMalformed_IsInvalid(string key)
    {
        var assessment = Assess(Hint(HintKind.History, key: key));

        Assert.Equal(HintCode.Invalid, assessment.Code);
    }

    /// <summary>
    /// A history hint for a user whose mapping names no valid local user is not mapped.
    /// True: history is never written to a user that does not exist here.
    /// False: a broken mapping passes triage and the apply fails on every retry.
    /// </summary>
    [Fact]
    public void Assess_HistoryUserMappedToAnInvalidLocalUser_IsNotMapped()
    {
        var origin = QueueSamples.Server();
        origin.UserMappings[0].LocalUserId = "not-an-id";

        var assessment = Assess(Hint(HintKind.History, key: HintProtocol.HistoryKey(QueueSamples.UserId, QueueSamples.ItemId)), origin);

        Assert.Equal(HintCode.NotMapped, assessment.Code);
    }

    /// <summary>
    /// A history hint outside every mapped library, or inside a filtered folder, is declined by path.
    /// True: history follows the same library rules as the items it belongs to.
    /// False: plays of unmapped or filtered items are stored and never apply.
    /// </summary>
    [Theory]
    [InlineData("/elsewhere/a.mkv", HintCode.NotMapped)]
    [InlineData("/media/movies/Hidden/a.mkv", HintCode.Filtered)]
    public void Assess_HistoryPathUnmappedOrFiltered_IsDeclined(string path, HintCode expected)
    {
        var origin = QueueSamples.Server();
        origin.LibraryMappings[0].FilterMode = LibraryFilterMode.Blacklist;
        origin.LibraryMappings[0].FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });

        var assessment = Assess(Hint(HintKind.History, path, HintProtocol.HistoryKey(QueueSamples.UserId, QueueSamples.ItemId)), origin);

        Assert.Equal(expected, assessment.Code);
    }

    /// <summary>
    /// A history hint is weighed against the stamp held for the local user and the local item.
    /// True: a play this server already holds is answered as current and not applied again.
    /// False: the stamp is looked up under the origin's ids, never found, and every hint is queued.
    /// </summary>
    [Fact]
    public void Assess_HistoryStampAlreadyHeld_IsAlreadyCurrent()
    {
        var local = _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        var at = Now.AddMinutes(-10);
        _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = HintProtocol.HistoryKey(QueueSamples.LocalUserId, local.Id), ServerId = OriginId, Timestamp = at });

        var assessment = Assess(Hint(HintKind.History, key: HintProtocol.HistoryKey(QueueSamples.UserId, QueueSamples.ItemId), at: at));

        Assert.Equal(HintCode.AlreadyCurrent, assessment.Code);
    }

    /// <summary>
    /// A people hint for a person who exists here is queued, or answered as current when its stamp is held.
    /// True: a person's edit applies once.
    /// False: the same edit is applied on every delivery, or never.
    /// </summary>
    [Theory]
    [InlineData(false, HintCode.Queued)]
    [InlineData(true, HintCode.AlreadyCurrent)]
    public void Assess_PersonExists_IsQueuedUnlessItsStampIsHeld(bool stampHeld, HintCode expected)
    {
        _local.AddPerson("Jane Doe");
        var at = Now.AddMinutes(-10);
        if (stampHeld)
        {
            _versions.Set(new ObjectVersion { Kind = HintKind.People, Key = HintProtocol.PeopleKey("Jane Doe"), ServerId = OriginId, Timestamp = at });
        }

        var assessment = Assess(Hint(HintKind.People, path: null, key: "Jane Doe", at: at));

        Assert.Equal(expected, assessment.Code);
        Assert.Null(assessment.ParkKey);
    }

    /// <summary>
    /// A playlist hint whose key or playlist id is not an id is invalid, and one whose owner is not an id is not mapped.
    /// True: a malformed playlist hint is answered before any playlist is read.
    /// False: the apply fails later on an id it cannot parse.
    /// </summary>
    [Theory]
    [InlineData("not-an-id", "55555555555555555555555555555555", "22222222222222222222222222222222", HintCode.Invalid)]
    [InlineData("44444444444444444444444444444444", "not-an-id", "22222222222222222222222222222222", HintCode.Invalid)]
    [InlineData("44444444444444444444444444444444", null, "22222222222222222222222222222222", HintCode.Invalid)]
    [InlineData("44444444444444444444444444444444", "55555555555555555555555555555555", "not-an-id", HintCode.NotMapped)]
    [InlineData("44444444444444444444444444444444", "55555555555555555555555555555555", null, HintCode.NotMapped)]
    public void Assess_PlaylistIdsMalformed_AreDeclined(string key, string? itemId, string? userId, HintCode expected)
    {
        var hint = Hint(HintKind.Playlists, path: null, key: key);
        hint.ItemId = itemId;
        hint.UserId = userId;

        var assessment = Assess(hint);

        Assert.Equal(expected, assessment.Code);
    }

    /// <summary>
    /// The park key of a missing person is built from the folded name, and of a missing item from its local path.
    /// True: the hint wakes when that person or that item appears.
    /// False: the hint waits under a key nothing wakes.
    /// </summary>
    [Fact]
    public void MissingLocal_PersonOrItemMissing_GivesItsParkKey()
    {
        var origin = QueueSamples.Server();

        var person = _triage.MissingLocal(origin, HintKind.People, null, "Jane Doe");
        var metadata = _triage.MissingLocal(origin, HintKind.Metadata, "/media/movies/a.mkv", "key");
        var history = _triage.MissingLocal(origin, HintKind.History, "/media/movies/a.mkv", "key");

        Assert.Equal("person:JANE DOE", person);
        Assert.Equal("path:" + QueueSamples.LocalPath("a.mkv"), metadata);
        Assert.Equal(metadata, history);
    }

    /// <summary>
    /// Nothing is missing when the person or item exists, the path is not mapped, or the kind needs no local object.
    /// True: only a hint that truly waits for something is parked.
    /// False: content hints, which bring their own item, park forever.
    /// </summary>
    [Fact]
    public void MissingLocal_PresentUnmappedOrNotNeeded_GivesNull()
    {
        var origin = QueueSamples.Server();
        _local.AddPerson("Jane Doe");
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));

        Assert.Null(_triage.MissingLocal(origin, HintKind.People, null, "Jane Doe"));
        Assert.Null(_triage.MissingLocal(origin, HintKind.Metadata, "/media/movies/a.mkv", "key"));
        Assert.Null(_triage.MissingLocal(origin, HintKind.Metadata, "/elsewhere/a.mkv", "key"));
        Assert.Null(_triage.MissingLocal(origin, HintKind.Metadata, null, "key"));
        Assert.Null(_triage.MissingLocal(origin, HintKind.Content, "/media/movies/missing.mkv", "key"));
        Assert.Null(_triage.MissingLocal(origin, HintKind.Playlists, "/media/movies/missing.mkv", "key"));
    }

    /// <summary>
    /// A missing origin or key is refused.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference inside the worker's pass.
    /// </summary>
    [Fact]
    public void MissingLocal_NullOriginOrKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _triage.MissingLocal(null!, HintKind.People, null, "Jane Doe"));
        Assert.Throws<ArgumentNullException>(() => _triage.MissingLocal(QueueSamples.Server(), HintKind.People, null, null!));
    }

    /// <summary>
    /// A blank park key is refused.
    /// True: a row parked without a key is noticed.
    /// False: a blank key is read as a person and quietly never present.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Present_BlankParkKey_Throws(string parkKey)
    {
        Assert.Throws<ArgumentException>(() => _triage.Present(parkKey, "Jane Doe"));
    }

    /// <summary>
    /// A person's park key is present when the named person exists, and never without a name.
    /// True: a parked people hint wakes as soon as its person is here.
    /// False: it waits out its window although the person arrived.
    /// </summary>
    [Fact]
    public void Present_PersonKey_ChecksThePersonByName()
    {
        _local.AddPerson("Jane Doe");
        var parkKey = HintTriage.PersonParkKey("Jane Doe");

        Assert.True(_triage.Present(parkKey, "Jane Doe"));
        Assert.False(_triage.Present(parkKey, "Someone Else"));
        Assert.False(_triage.Present(parkKey, null));
        Assert.False(_triage.Present(parkKey, "  "));
    }

    /// <summary>
    /// The park keys have the shapes the store and the observer both build.
    /// True: the key a hint parks under equals the key the arriving item wakes.
    /// False: the two sides spell the key differently and nothing ever wakes.
    /// </summary>
    [Fact]
    public void ParkKeys_PathAndPerson_HaveTheirPrefixes()
    {
        Assert.Equal("path:/local/movies/a.mkv", HintTriage.PathParkKey("/local/movies/a.mkv"));
        Assert.Equal("person:JANE DOE", HintTriage.PersonParkKey("  Jane Doe "));
    }

    /// <summary>
    /// A filter leaves a path out only when it has entries, is not allow all, and the path fails it.
    /// True: an empty or disabled filter lets everything through.
    /// False: a block list with nothing on it, or a null list, blocks the whole library.
    /// </summary>
    [Theory]
    [InlineData(LibraryFilterMode.AllowAll, true, "/media/movies/Hidden/a.mkv", false)]
    [InlineData(LibraryFilterMode.Blacklist, false, "/media/movies/Hidden/a.mkv", false)]
    [InlineData(LibraryFilterMode.Blacklist, true, "/media/movies/Hidden/a.mkv", true)]
    [InlineData(LibraryFilterMode.Blacklist, true, "/media/movies/Other/a.mkv", false)]
    [InlineData(LibraryFilterMode.Whitelist, true, "/media/movies/Hidden/a.mkv", false)]
    [InlineData(LibraryFilterMode.Whitelist, true, "/media/movies/Other/a.mkv", true)]
    [InlineData(LibraryFilterMode.Whitelist, true, "", false)]
    [InlineData(LibraryFilterMode.Whitelist, true, null, false)]
    public void IsFiltered_EachModeAndPath_LeavesOutOnlyWhatTheFilterNames(LibraryFilterMode mode, bool hasEntry, string? path, bool filtered)
    {
        var mapping = new LibraryMapping { SourceRootPath = "/media/movies", LocalRootPath = "/local/movies", FilterMode = mode };
        if (hasEntry)
        {
            mapping.FilteredItems.Add(new FilteredItem { Path = "/media/movies/Hidden" });
        }

        var result = HintTriage.IsFiltered(mapping, path);

        Assert.Equal(filtered, result);
    }

    /// <summary>
    /// A mapping with no filter list at all filters nothing, and a missing mapping is refused.
    /// True: a mapping saved before filters existed still syncs.
    /// False: a null list throws inside triage and every hint for that library fails.
    /// </summary>
    [Fact]
    public void IsFiltered_NullListOrNullMapping_FiltersNothingOrThrows()
    {
        var mapping = new LibraryMapping { SourceRootPath = "/media/movies", FilterMode = LibraryFilterMode.Whitelist, FilteredItems = null! };

        Assert.False(HintTriage.IsFiltered(mapping, "/media/movies/a.mkv"));
        Assert.Throws<ArgumentNullException>(() => HintTriage.IsFiltered(null!, "/media/movies/a.mkv"));
    }
}
