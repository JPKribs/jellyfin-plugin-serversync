using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The setup routes call another server only at a URL the SSRF guard passes, and report every failure in words the page can show.
/// </summary>
public sealed class ConfigurationControllerConfigurationTests : IDisposable
{
    private const string Url = "http://127.0.0.1:8096";
    private const string UserId = "22222222-2222-2222-2222-222222222222";
    private const string ItemId = "33333333-3333-3333-3333-333333333333";

    private readonly ControllerTestHarness _harness = new();
    private readonly List<string> _queries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerConfigurationTests"/> class, with a peer that answers as a Jellyfin server with Server Sync.
    /// </summary>
    public ConfigurationControllerConfigurationTests()
    {
        _harness.Peer = request =>
        {
            _queries.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath switch
            {
                "/System/Info" => ControllerTestHarness.Json("{\"ServerName\":\"Peer\",\"Id\":\"" + ControllerTestHarness.PeerId + "\"}"),
                "/Users/Me" => new HttpResponseMessage(HttpStatusCode.BadRequest),
                "/Sync/Peer/Capabilities" => ControllerTestHarness.Json("{\"PluginVersion\":\"0.0.0-test\"}"),
                "/Library/VirtualFolders" => ControllerTestHarness.Json("[{\"Name\":\"Movies\",\"ItemId\":\"lib-1\",\"Locations\":[\"/media/movies\"]},{\"Name\":null,\"ItemId\":null,\"Locations\":null}]"),
                "/Users" => ControllerTestHarness.Json("[{\"Id\":\"" + UserId + "\",\"Name\":\"alice\"}]"),
                "/Items" => ControllerTestHarness.Json("{\"Items\":[{\"Id\":\"" + ItemId + "\",\"Name\":\"Film\",\"ProductionYear\":2020,\"Overview\":\"About\",\"Path\":\"/media/movies/Film\",\"Type\":\"Movie\"}],\"TotalRecordCount\":7,\"StartIndex\":0}"),
                "/Users/AuthenticateByName" => ControllerTestHarness.Json("{\"AccessToken\":\"token-1\",\"ServerId\":\"" + ControllerTestHarness.PeerId + "\",\"User\":{\"Name\":\"alice\",\"Id\":\"" + UserId + "\",\"Policy\":{\"IsAdministrator\":true,\"EnableContentDownloading\":false}}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        };
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A Server URL the SSRF guard refuses is reported as a failed test, and the server is never called.
    /// True: an empty, malformed, non HTTP, link local, or plain HTTP public address reaches no client.
    /// False: the test button could be used to make this server call an address it must not.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("not a url", true)]
    [InlineData("ftp://127.0.0.1/", true)]
    [InlineData("http://127.0.0.1:8096/../admin", true)]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("http://203.0.113.10:8096", true)]
    [InlineData("http://10.0.0.5:8096", false)]
    public async Task TestConnection_UrlTheGuardRefuses_FailsWithoutCalling(string url, bool allowPrivateNetwork)
    {
        var request = new TestConnectionRequest { ServerUrl = url, ApiKey = "key", AllowPrivateNetwork = allowPrivateNetwork };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal(ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork), body.Message);
        Assert.Empty(_harness.Clients.Created);
        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// A blank API Key is reported as a failed test, and the server is never called.
    /// True: the page asks for the key.
    /// False: an unauthenticated call would be made and its error shown instead.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestConnection_BlankApiKey_AsksForTheKey(string apiKey)
    {
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = apiKey };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal("Enter the API Key.", body.Message);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// A server that answers is reported with its name, its ID, and whether its Server Sync allows this key.
    /// True: the page fills the entry from the answer and shows the sync access.
    /// False: a working server would be shown as failed, or without what the entry needs.
    /// </summary>
    [Fact]
    public async Task TestConnection_ServerAnswers_ReportsTheServerAndItsSyncAccess()
    {
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Success);
        Assert.Equal("Peer", body.ServerName);
        Assert.Equal(ControllerTestHarness.PeerId, body.ServerId);
        Assert.Equal("Allowed", body.SyncAccess);
        Assert.False(string.IsNullOrEmpty(body.SyncAccessMessage));
    }

    /// <summary>
    /// The client is built for the normalized URL, which keeps a sub path and a port and drops a trailing slash.
    /// True: a server behind a reverse proxy sub path is called at that path.
    /// False: the sub path would be lost, and every call would miss the server.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:8096/", "http://127.0.0.1:8096")]
    [InlineData("http://127.0.0.1:8096/jellyfin/", "http://127.0.0.1:8096/jellyfin")]
    [InlineData("HTTP://127.0.0.1", "http://127.0.0.1")]
    [InlineData("https://203.0.113.10/", "https://203.0.113.10")]
    [InlineData("http://[::1]:8096", "http://[::1]:8096")]
    public async Task TestConnection_Url_IsNormalizedBeforeTheClientIsBuilt(string url, string normalized)
    {
        var request = new TestConnectionRequest { ServerUrl = url, ApiKey = "key", AllowPrivateNetwork = false == url.StartsWith("https", StringComparison.Ordinal) };

        await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var built = Assert.Single(_harness.Clients.Created);
        Assert.Equal(normalized, built.Url);
        Assert.Equal("key", built.ApiKey);
        Assert.Equal(request.AllowPrivateNetwork, built.AllowPrivateNetwork);
    }

    /// <summary>
    /// A refusal from the client factory's own guard is reported as a failed test with the factory's reason.
    /// True: the page shows why the address was refused.
    /// False: the refusal would surface as an unhandled error.
    /// </summary>
    [Fact]
    public async Task TestConnection_FactoryRefuses_ReportsItsReason()
    {
        _harness.Clients.Refusal = "that address is not allowed";
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal("that address is not allowed", body.Message);
        Assert.Equal("that address is not allowed", body.ErrorMessage);
    }

    /// <summary>
    /// A server that answers with an error is reported as a failed test, and its sync access is not asked for.
    /// True: the page shows the failure and no second call is made to a server that did not answer.
    /// False: a broken server would be shown as connected.
    /// </summary>
    [Fact]
    public async Task TestConnection_ServerAnswersWithAnError_FailsAndSkipsTheSyncCheck()
    {
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.False(string.IsNullOrEmpty(body.Message));
        Assert.Null(body.SyncAccess);
        Assert.DoesNotContain("/Sync/Peer/Capabilities", _harness.PeerPaths);
    }

    /// <summary>
    /// A server that never answers is reported as timed out.
    /// True: the page says the server may be unreachable.
    /// False: a timeout would surface as an unhandled error, or blame the API Key.
    /// </summary>
    [Fact]
    public async Task TestConnection_ServerNeverAnswers_ReportsATimeout()
    {
        _harness.Peer = _ => throw new TaskCanceledException("timed out", new TimeoutException());
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().TestConnection(request, CancellationToken.None);

        var body = Assert.IsType<ConnectionTestResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Contains("timed out", body.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled test is not reported as a result.
    /// True: the cancellation reaches the caller.
    /// False: a request the browser gave up on would be reported as a bad API Key.
    /// </summary>
    [Fact]
    public async Task TestConnection_Cancelled_Throws()
    {
        _harness.Peer = _ => throw new OperationCanceledException();
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _harness.Configuration().TestConnection(request, new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// A sign in is refused before any call when the URL, the username, or the password is missing.
    /// True: the page hears which field to fill, and no credentials leave this server.
    /// False: a blank form would post empty credentials to the other server.
    /// </summary>
    [Theory]
    [InlineData("", "alice", "pw", "URL cannot be empty")]
    [InlineData(Url, " ", "pw", "Enter the username.")]
    [InlineData(Url, "alice", "", "Enter the password.")]
    public async Task Authenticate_MissingField_FailsWithoutCalling(string url, string username, string password, string message)
    {
        var request = new AuthenticateRequest { ServerUrl = url, Username = username, Password = password };

        var result = await _harness.Configuration().Authenticate(request, CancellationToken.None);

        var body = Assert.IsType<AuthenticateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal(message, body.Message);
        Assert.Empty(_harness.HttpClients.Names);
        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// A sign in the server accepts answers with the token, the server's name and ID, and its sync access.
    /// True: the page stores the token as the entry's key and fills the entry.
    /// False: a correct password would not set up the entry.
    /// </summary>
    [Fact]
    public async Task Authenticate_ServerAccepts_AnswersWithTheTokenAndTheServer()
    {
        var request = new AuthenticateRequest { ServerUrl = Url + "/", Username = "alice", Password = "pw" };

        var result = await _harness.Configuration().Authenticate(request, CancellationToken.None);

        var body = Assert.IsType<AuthenticateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Success);
        Assert.Equal("token-1", body.AccessToken);
        Assert.Equal("alice", body.Username);
        Assert.Equal("Peer", body.ServerName);
        Assert.Equal(ControllerTestHarness.PeerId, body.ServerId);
        Assert.Equal("Signed in.", body.Message);
        Assert.Equal("Allowed", body.SyncAccess);
        Assert.Equal(false, body.CanDownload);
        Assert.Equal("token-1", Assert.Single(_harness.Clients.Created).ApiKey);
    }

    /// <summary>
    /// The sign in goes through the client that allows private networks only when the entry does.
    /// True: an entry that forbids private networks signs in through the public only client.
    /// False: the sign in would skip the address check the entry asked for.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:8096", true, SourceServerClient.HttpClientName)]
    [InlineData("https://203.0.113.10", false, SourceServerClient.PublicHttpClientName)]
    public async Task Authenticate_AllowPrivateNetwork_PicksTheNamedClient(string url, bool allowPrivateNetwork, string clientName)
    {
        var request = new AuthenticateRequest { ServerUrl = url, Username = "alice", Password = "pw", AllowPrivateNetwork = allowPrivateNetwork };

        await _harness.Configuration().Authenticate(request, CancellationToken.None);

        Assert.Equal(new[] { clientName }, _harness.HttpClients.Names);
    }

    /// <summary>
    /// Wrong credentials are reported as a failed sign in, and no client is built with a token.
    /// True: the page says the username or password is wrong.
    /// False: a refused sign in would go on to test a connection with no token.
    /// </summary>
    [Fact]
    public async Task Authenticate_ServerRefusesTheCredentials_FailsWithTheReason()
    {
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var request = new AuthenticateRequest { ServerUrl = Url, Username = "alice", Password = "wrong" };

        var result = await _harness.Configuration().Authenticate(request, CancellationToken.None);

        var body = Assert.IsType<AuthenticateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal("Invalid username or password", body.Message);
        Assert.Null(body.AccessToken);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// A sign in that worked but whose client the factory refuses is reported as failed with the factory's reason.
    /// True: the page shows why the address was refused.
    /// False: the page would show a signed in entry that no run can reach.
    /// </summary>
    [Fact]
    public async Task Authenticate_FactoryRefusesAfterSignIn_FailsWithItsReason()
    {
        _harness.Clients.Refusal = "that address is not allowed";
        var request = new AuthenticateRequest { ServerUrl = Url, Username = "alice", Password = "pw" };

        var result = await _harness.Configuration().Authenticate(request, CancellationToken.None);

        var body = Assert.IsType<AuthenticateResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Success);
        Assert.Equal("that address is not allowed", body.Message);
    }

    /// <summary>
    /// The libraries and users routes answer 400 with the reason when the URL is refused or the API Key is blank.
    /// True: the page shows the reason, and the server is never called.
    /// False: the pickers could be used to call an address the guard refuses.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254/", "key", null)]
    [InlineData(Url, " ", "Enter the API Key.")]
    public async Task GetSourceLibrariesAndUsers_RefusedUrlOrBlankKey_Answer400(string url, string apiKey, string? message)
    {
        var request = new TestConnectionRequest { ServerUrl = url, ApiKey = apiKey };
        var expected = message ?? ConfigurationUtilities.ValidateServerUrlForSsrf(url, true);

        var libraries = await _harness.Configuration().GetSourceLibraries(request, CancellationToken.None);
        var users = await _harness.Configuration().GetSourceUsers(request, CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<BadRequestObjectResult>(libraries.Result).Value);
        Assert.Equal(expected, Assert.IsType<BadRequestObjectResult>(users.Result).Value);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// The libraries route maps each library's ID, name, and folders, with empty values where the server sent none.
    /// True: the library picker lists what the server has, and a library with missing fields does not break it.
    /// False: the picker would be empty, or fail on a library without folders.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraries_ServerAnswers_MapsEachLibrary()
    {
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().GetSourceLibraries(request, CancellationToken.None);

        var libraries = Assert.IsType<List<LibraryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, libraries.Count);
        Assert.Equal(("lib-1", "Movies"), (libraries[0].Id, libraries[0].Name));
        Assert.Equal(new[] { "/media/movies" }, libraries[0].Locations);
        Assert.Equal((string.Empty, string.Empty), (libraries[1].Id, libraries[1].Name));
        Assert.Empty(libraries[1].Locations);
    }

    /// <summary>
    /// A standard user's key falls back to that user's views, using the user stored on the entry when the request names none.
    /// True: an entry signed in as a standard user still lists the libraries that user can see.
    /// False: the picker would be empty for every standard user entry.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraries_AdminListRefused_FallsBackToTheStoredUsersViews()
    {
        var entry = _harness.AddPeer();
        entry.AuthenticatedUserId = "stored-user";
        _harness.Peer = request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/stored-user/Views" => ControllerTestHarness.Json("{\"Items\":[{\"Id\":\"view-1\",\"Name\":\"Shows\",\"CollectionType\":\"tvshows\"},{\"Id\":\"view-2\",\"Name\":\"Playlists\",\"CollectionType\":\"playlists\"}]}"),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        };
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key", ServerKey = entry.Key };

        var result = await _harness.Configuration().GetSourceLibraries(request, CancellationToken.None);

        var library = Assert.Single(Assert.IsType<List<LibraryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(("view-1", "Shows"), (library.Id, library.Name));
    }

    /// <summary>
    /// The users route maps each user's ID and name.
    /// True: the user mapping picker lists the server's users.
    /// False: no user could be mapped.
    /// </summary>
    [Fact]
    public async Task GetSourceUsers_ServerAnswers_MapsEachUser()
    {
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var result = await _harness.Configuration().GetSourceUsers(request, CancellationToken.None);

        var user = Assert.Single(Assert.IsType<List<UserInfoDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal((UserId, "alice"), (user.Id, user.Name));
    }

    /// <summary>
    /// A server that fails to list its libraries or users answers with an empty list.
    /// True: the pickers show nothing to choose.
    /// False: a failing server would break the settings page.
    /// </summary>
    [Fact]
    public async Task GetSourceLibrariesAndUsers_ServerAnswersWithAnError_AnswerEmptyLists()
    {
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var libraries = await _harness.Configuration().GetSourceLibraries(request, CancellationToken.None);
        var users = await _harness.Configuration().GetSourceUsers(request, CancellationToken.None);

        Assert.Empty(Assert.IsType<List<LibraryDto>>(Assert.IsType<OkObjectResult>(libraries.Result).Value));
        Assert.Empty(Assert.IsType<List<UserInfoDto>>(Assert.IsType<OkObjectResult>(users.Result).Value));
    }

    /// <summary>
    /// A refusal from the client factory answers 400 with the factory's reason on both picker routes.
    /// True: the page shows why the address was refused.
    /// False: the refusal would surface as an unhandled error.
    /// </summary>
    [Fact]
    public async Task GetSourceLibrariesAndUsers_FactoryRefuses_Answer400WithItsReason()
    {
        _harness.Clients.Refusal = "that address is not allowed";
        var request = new TestConnectionRequest { ServerUrl = Url, ApiKey = "key" };

        var libraries = await _harness.Configuration().GetSourceLibraries(request, CancellationToken.None);
        var users = await _harness.Configuration().GetSourceUsers(request, CancellationToken.None);

        Assert.Equal("that address is not allowed", Assert.IsType<BadRequestObjectResult>(libraries.Result).Value);
        Assert.Equal("that address is not allowed", Assert.IsType<BadRequestObjectResult>(users.Result).Value);
    }

    /// <summary>
    /// Browsing a library is refused with 400 when no library is named, the ID is not one, or the entry is not set up.
    /// True: the picker hears what is wrong and the server is never called.
    /// False: a malformed request would reach the server, or throw.
    /// </summary>
    [Theory]
    [InlineData("", true, "Select a library.")]
    [InlineData("  ", true, "Select a library.")]
    [InlineData(null, true, "Select a library.")]
    [InlineData("not-an-id", true, "The library ID is not valid.")]
    [InlineData(ItemId, false, "The server is not configured.")]
    public async Task GetSourceLibraryItems_BadLibraryOrNoServer_Answers400(string? libraryId, bool configured, string message)
    {
        if (configured)
        {
            _harness.AddPeer();
        }

        var result = await _harness.Configuration().GetSourceLibraryItems(libraryId!);

        Assert.Equal(message, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// An entry with no API Key counts as not set up.
    /// True: the picker is refused before any call.
    /// False: an entry without a key would be browsed with no credentials.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraryItems_EntryWithoutAKey_Answers400()
    {
        _harness.AddPeer().ApiKey = string.Empty;

        var result = await _harness.Configuration().GetSourceLibraryItems(ItemId);

        Assert.Equal("The server is not configured.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// A library's items are mapped with the server's total count.
    /// True: the picker shows each item's name, year, path, and type, and pages by the real total.
    /// False: the picker would show blank rows or stop after one page.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraryItems_ServerAnswers_MapsTheItemsAndTheTotal()
    {
        _harness.AddPeer();

        var result = await _harness.Configuration().GetSourceLibraryItems(ItemId);

        var body = Assert.IsType<SourceLibraryItemsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var item = Assert.Single(body.Items);
        Assert.Equal(7, body.TotalCount);
        Assert.Equal(Guid.Parse(ItemId).ToString("N"), item.Id);
        Assert.Equal(("Film", 2020, "About", "/media/movies/Film", "Movie"), (item.Name, item.Year, item.Overview, item.Path, item.Type));
    }

    /// <summary>
    /// The page size is held between 1 and 200 and the start is never negative, and skip and take win over startIndex and limit.
    /// True: the largest page a caller can ask the source for is 200 items.
    /// False: one request could ask the source for its whole library.
    /// </summary>
    [Theory]
    [InlineData(0, 50, null, null, "startIndex=0", "limit=50")]
    [InlineData(-5, 100000, null, null, "startIndex=0", "limit=200")]
    [InlineData(10, 0, null, null, "startIndex=10", "limit=1")]
    [InlineData(10, 20, 30, 40, "startIndex=30", "limit=40")]
    [InlineData(10, 20, -1, int.MaxValue, "startIndex=0", "limit=200")]
    [InlineData(10, 20, 5, int.MinValue, "startIndex=5", "limit=1")]
    public async Task GetSourceLibraryItems_Paging_IsClamped(int startIndex, int limit, int? skip, int? take, string expectedStart, string expectedLimit)
    {
        _harness.AddPeer();

        await _harness.Configuration().GetSourceLibraryItems(ItemId, startIndex: startIndex, limit: limit, skip: skip, take: take);

        var query = Assert.Single(_queries, q => q.StartsWith("/Items", StringComparison.Ordinal));
        Assert.Contains(expectedStart, query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedLimit, query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collections and playlists are browsed without a library ID, each asking the source for its own item type.
    /// True: the filter picker lists collections or playlists across the whole server.
    /// False: both would be refused for want of a library.
    /// </summary>
    [Theory]
    [InlineData(true, false, "BoxSet")]
    [InlineData(false, true, "Playlist")]
    public async Task GetSourceLibraryItems_CollectionsOrPlaylists_NeedNoLibrary(bool collections, bool playlists, string itemType)
    {
        _harness.AddPeer();

        var result = await _harness.Configuration().GetSourceLibraryItems(string.Empty, collections: collections, playlists: playlists);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Contains("includeItemTypes=" + itemType, Assert.Single(_queries, q => q.StartsWith("/Items", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A source that answers with an error gives an empty page.
    /// True: the picker shows no items.
    /// False: a failing source would break the picker.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraryItems_ServerAnswersWithAnError_AnswersAnEmptyPage()
    {
        _harness.AddPeer();
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await _harness.Configuration().GetSourceLibraryItems(ItemId);

        var body = Assert.IsType<SourceLibraryItemsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(body.Items);
        Assert.Equal(0, body.TotalCount);
    }

    /// <summary>
    /// A refusal from the client factory answers 400 without the factory's reason.
    /// True: the picker hears that the items could not be loaded.
    /// False: the refusal would surface as an unhandled error.
    /// </summary>
    [Fact]
    public async Task GetSourceLibraryItems_FactoryRefuses_Answers400()
    {
        _harness.AddPeer();
        _harness.Clients.Refusal = "that address is not allowed";

        var result = await _harness.Configuration().GetSourceLibraryItems(ItemId);

        Assert.Equal("Could not load the items from the server.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// Each setup route requires elevation and answers one verb at its own path.
    /// True: only an administrator can make this server call another server or sign in to one.
    /// False: any signed in user could probe addresses or harvest a token through these routes.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.TestConnection), "POST", "TestConnection")]
    [InlineData(nameof(ConfigurationController.Authenticate), "POST", "Authenticate")]
    [InlineData(nameof(ConfigurationController.GetSourceLibraries), "POST", "GetSourceLibraries")]
    [InlineData(nameof(ConfigurationController.GetSourceUsers), "POST", "GetSourceUsers")]
    [InlineData(nameof(ConfigurationController.GetSourceLibraryItems), "GET", "SourceLibraryItems")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }
}
