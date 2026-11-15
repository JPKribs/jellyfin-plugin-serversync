using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// Forgetting a server entry removes that entry's rows, hints, links, and secrets, and leaves every other entry's and every copied file alone.
/// </summary>
public sealed class ServersControllerTests : IDisposable
{
    private const string Gone = "gone";
    private const string Kept = "kept";

    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A blank key is refused before anything is removed.
    /// True: the caller hears 400 and every table keeps its rows.
    /// False: a blank key would reach the tables, or throw.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForgetRows_BlankKey_Answers400AndRemovesNothing(string? key)
    {
        Seed(Gone);

        var result = Controller().ForgetRows(key!, serverId: null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(1, _harness.Content.Count());
        Assert.Single(_harness.Outbound.GetAll());
    }

    /// <summary>
    /// Forgetting an entry removes its rows from every table and reports how many went from each.
    /// True: nothing of the forgotten server is left to sync, and the counts match what was there.
    /// False: rows of a removed server would keep syncing against a server that is no longer listed.
    /// </summary>
    [Fact]
    public void ForgetRows_EntryWithRowsEverywhere_RemovesThemAndCountsEach()
    {
        Seed(Gone);
        Seed(Kept);

        var result = Controller().ForgetRows(Gone, serverId: null);

        var removed = Assert.IsType<ServerRowsRemoved>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, removed.Content);
        Assert.Equal(1, removed.History);
        Assert.Equal(1, removed.Metadata);
        Assert.Equal(1, removed.People);
        Assert.Equal(1, removed.Users);
        Assert.Equal(1, removed.Hints);
    }

    /// <summary>
    /// Forgetting one entry leaves another entry's rows and hints in place.
    /// True: each table still holds the other server's row.
    /// False: removing one server would wipe every server's sync state.
    /// </summary>
    [Fact]
    public void ForgetRows_AnotherEntrysRows_AreKept()
    {
        Seed(Gone);
        Seed(Kept);

        Controller().ForgetRows(Gone, serverId: null);

        Assert.Equal(Kept, Assert.Single(_harness.Content.GetAllStrict()).ServerKey);
        Assert.Equal(Kept, Assert.Single(_harness.History.GetAllStrict()).ServerKey);
        Assert.Equal(Kept, Assert.Single(_harness.Metadata.GetAllStrict()).ServerKey);
        Assert.Equal(Kept, Assert.Single(_harness.People.GetAllStrict()).ServerKey);
        Assert.Equal(Kept, Assert.Single(_harness.Users.GetAllStrict()).ServerKey);
        Assert.Equal(Kept, Assert.Single(_harness.Outbound.GetAll()).PeerKey);
    }

    /// <summary>
    /// The hints a server sent go only when its Jellyfin ID is given, and are counted with the hints owed to it.
    /// True: with the ID both ways are cleared, and without it the received hints stay.
    /// False: a forgotten server's hints would still be applied, or hints of an unnamed server would be dropped.
    /// </summary>
    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(" ", 1, 1)]
    [InlineData(ControllerTestHarness.PeerId, 2, 0)]
    [InlineData("0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F", 2, 0)]
    public void ForgetRows_ServerId_DecidesWhetherReceivedHintsGo(string? serverId, int expectedHints, int inboundLeft)
    {
        Seed(Gone);
        _harness.Inbound.Enqueue(new InboundHint { HintId = ControllerTestHarness.PeerId + ":1", OriginServerId = ControllerTestHarness.PeerId, Kind = HintKind.Metadata, Key = "k", VersionServerId = ControllerTestHarness.PeerId, VersionTimestamp = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow, NextAttempt = DateTime.UtcNow });

        var result = Controller().ForgetRows(Gone, serverId);

        var removed = Assert.IsType<ServerRowsRemoved>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expectedHints, removed.Hints);
        Assert.Equal(inboundLeft, _harness.Inbound.Count());
    }

    /// <summary>
    /// Forgetting an entry forgets the secrets shared with it, and no other entry's.
    /// True: a server added again under that key must pair again, and other pairings hold.
    /// False: a removed server's secret would still prove a sender, or every pairing would be lost.
    /// </summary>
    [Fact]
    public void ForgetRows_Pairings_OnlyTheForgottenEntrysAreRemoved()
    {
        _harness.Pairings.SetInbound(Gone, "issued-to-gone");
        _harness.Pairings.SetOutbound(Gone, "issued-by-gone");
        _harness.Pairings.SetInbound(Kept, "issued-to-kept");

        Controller().ForgetRows(Gone, serverId: null);

        Assert.Null(_harness.Pairings.GetInbound(Gone));
        Assert.Null(_harness.Pairings.GetOutbound(Gone));
        Assert.Equal("issued-to-kept", _harness.Pairings.GetInbound(Kept));
    }

    /// <summary>
    /// A key with no rows anywhere answers 200 with zeroes.
    /// True: forgetting an entry twice, or one never synced, is harmless.
    /// False: the dashboard would show an error after removing a server that had no rows.
    /// </summary>
    [Fact]
    public void ForgetRows_UnknownKey_AnswersZeroes()
    {
        Seed(Kept);

        var result = Controller().ForgetRows("never-seen", ControllerTestHarness.PeerId);

        var removed = Assert.IsType<ServerRowsRemoved>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, removed.Content + removed.History + removed.Metadata + removed.People + removed.Users + removed.Hints);
        Assert.Equal(1, _harness.Content.Count());
    }

    /// <summary>
    /// A forgotten entry's link files under a library folder are deleted, and its copies are left on disk.
    /// True: no dead link stays in the library, and a downloaded file is never removed.
    /// False: the library would keep links that play nothing, or forgetting a server would delete media.
    /// </summary>
    [Fact]
    public void ForgetRows_WithTheLibraryManager_DeletesLinksAndLeavesCopies()
    {
        var root = Path.Combine(_harness.TempDir, "library");
        var link = WriteLink(Path.Combine(root, "Film (2020)", "Film (2020).strm"));
        var copy = Path.Combine(root, "Other (2021)", "Other (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "media");
        _harness.Content.Upsert(ContentRow(Gone, "link-1", link, StorageMode.Link));
        _harness.Content.Upsert(ContentRow(Gone, "copy-1", copy, StorageMode.Copy));
        _harness.LibraryStub.Answer("GetVirtualFolders", _ => new List<VirtualFolderInfo> { new() { Locations = new[] { root } } });

        Controller().ForgetRows(Gone, serverId: null, _harness.Library);

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(copy));
        Assert.Equal(0, _harness.Content.Count());
    }

    /// <summary>
    /// Link files stay on disk when no library manager is given, when the link lies outside every library folder, and when the row belongs to another entry.
    /// True: only a link of the forgotten entry inside a known library folder is ever deleted.
    /// False: forgetting a server could delete files outside the library or another server's links.
    /// </summary>
    [Theory]
    [InlineData(false, true, Gone)]
    [InlineData(true, false, Gone)]
    [InlineData(true, true, Kept)]
    public void ForgetRows_NoLibraryOutsideItOrAnotherEntry_LeavesTheLinkFile(bool withLibrary, bool insideRoot, string rowKey)
    {
        var root = Path.Combine(_harness.TempDir, "library");
        Directory.CreateDirectory(root);
        var link = WriteLink(Path.Combine(insideRoot ? root : Path.Combine(_harness.TempDir, "elsewhere"), "Film (2020)", "Film (2020).strm"));
        _harness.Content.Upsert(ContentRow(rowKey, "link-1", link, StorageMode.Link));
        _harness.LibraryStub.Answer("GetVirtualFolders", _ => new List<VirtualFolderInfo> { new() { Locations = new[] { root } } });

        Controller().ForgetRows(Gone, serverId: null, withLibrary ? _harness.Library : null);

        Assert.True(File.Exists(link));
    }

    /// <summary>
    /// A user's own .strm that a link row points at is not deleted.
    /// True: only a file that holds this server's link URL counts as a link to remove.
    /// False: forgetting a server would delete a file the user put in a link's place.
    /// </summary>
    [Fact]
    public void ForgetRows_StrmThatIsNotThisServersLink_IsLeft()
    {
        var root = Path.Combine(_harness.TempDir, "library");
        var path = Path.Combine(root, "Film (2020)", "Film (2020).strm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "http://example.com/stream.mkv");
        _harness.Content.Upsert(ContentRow(Gone, "link-1", path, StorageMode.Link));
        _harness.LibraryStub.Answer("GetVirtualFolders", _ => new List<VirtualFolderInfo> { new() { Locations = new[] { root } } });

        Controller().ForgetRows(Gone, serverId: null, _harness.Library);

        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The controller refuses to be built without its parts.
    /// True: a missing store is named when the controller is built.
    /// False: forgetting a server would fail halfway with a null reference, leaving some tables cleared.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        var h = _harness;
        var logger = NullLogger<ServersController>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ServersController(null!, h.History, h.Metadata, h.People, h.Users, h.Outbound, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, null!, h.Metadata, h.People, h.Users, h.Outbound, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, null!, h.People, h.Users, h.Outbound, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, null!, h.Users, h.Outbound, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, h.People, null!, h.Outbound, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, h.People, h.Users, null!, h.Inbound, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, h.People, h.Users, h.Outbound, null!, h.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, h.People, h.Users, h.Outbound, h.Inbound, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ServersController(h.Content, h.History, h.Metadata, h.People, h.Users, h.Outbound, h.Inbound, h.Pairings, null!));
    }

    /// <summary>
    /// The route is an elevated DELETE under Sync/Servers.
    /// True: only an administrator can forget a server, and only with a DELETE.
    /// False: any signed in user, or a stray GET, could wipe a server's sync state.
    /// </summary>
    [Fact]
    public void ForgetRows_Route_RequiresElevationAndDelete()
    {
        var controller = typeof(ServersController);
        var action = controller.GetMethod(nameof(ServersController.ForgetRows))!;

        Assert.Equal("RequiresElevation", controller.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("Sync/Servers", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(new[] { "DELETE" }, action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToArray());
        Assert.Equal("{key}/Rows", action.GetCustomAttribute<HttpDeleteAttribute>()!.Template);
    }

    private static SyncItem ContentRow(string serverKey, string id, string localPath, StorageMode mode) => new()
    {
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = id,
        SourcePath = "/src/" + id + ".mkv",
        SourceSize = 1,
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = localPath,
        Status = SyncStatus.Synced,
        StatusDate = DateTime.UtcNow,
        ServerKey = serverKey,
        StorageMode = mode
    };

    private static string WriteLink(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "http://127.0.0.1:8096/Sync/Link/link-1?k=secret");
        return path;
    }

    private void Seed(string serverKey)
    {
        _harness.Content.Upsert(ContentRow(serverKey, "item-" + serverKey, "/nowhere/" + serverKey + ".mkv", StorageMode.Copy));
        _harness.History.Upsert(new HistorySyncItem { SourceUserId = "user-" + serverKey, LocalUserId = "local", SourceItemId = "item-" + serverKey, SourceLibraryId = "lib", LocalLibraryId = "local", StatusDate = DateTime.UtcNow, ServerKey = serverKey });
        _harness.Metadata.Upsert(new MetadataSyncItem { SourceLibraryId = "lib-" + serverKey, LocalLibraryId = "local", SourceItemId = "item-" + serverKey, StatusDate = DateTime.UtcNow, ServerKey = serverKey });
        _harness.People.Upsert(new PeopleSyncItem { PersonName = "Person " + serverKey, StatusDate = DateTime.UtcNow, ServerKey = serverKey });
        _harness.Users.Upsert(new UserSyncItem { SourceUserId = "user-" + serverKey, LocalUserId = "local", PropertyCategory = UserPropertyCategory.Policy, StatusDate = DateTime.UtcNow, ServerKey = serverKey });
        _harness.Outbound.Enqueue(new OutboundHint { PeerKey = serverKey, Kind = HintKind.Metadata, Key = "obj", ItemId = "obj", VersionServerId = "me", VersionTimestamp = DateTime.UtcNow }, "me");
    }

    private ServersController Controller()
        => new(_harness.Content, _harness.History, _harness.Metadata, _harness.People, _harness.Users, _harness.Outbound, _harness.Inbound, _harness.Pairings, NullLogger<ServersController>.Instance);
}
