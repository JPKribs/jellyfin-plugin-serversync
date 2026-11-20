using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The client must report who a source is and what its API Key may do, sign in without leaking a failure as success, and fall back to one user's view only when the administrator route refuses.
/// </summary>
public sealed class SourceServerClientTests
{
    private const string UserId = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The constructor refuses a missing logger, HTTP client, or Server URL.
    /// True: a client is never built half wired.
    /// False: the first call would fail with a null reference far from its cause.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        using var http = new HttpClient(new ClientStubServer());

        Assert.Throws<ArgumentNullException>(() => new SourceServerClient(null!, http, ClientStubServer.Url, "k", "n", "v"));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClient(NullLogger<SourceServerClient>.Instance, null!, ClientStubServer.Url, "k", "n", "v"));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClient(NullLogger<SourceServerClient>.Instance, http, null!, "k", "n", "v"));
    }

    /// <summary>
    /// A Server URL saved with trailing slashes is called without them.
    /// True: every request path starts with exactly one slash.
    /// False: requests would go to "//Items", which a reverse proxy may refuse.
    /// </summary>
    [Fact]
    public void Constructor_TrailingSlashes_AreTrimmed()
    {
        using var client = new ClientStubServer().Client(ClientStubServer.Url + "//");

        Assert.Equal(ClientStubServer.Url, client.ServerUrl);
    }

    /// <summary>
    /// A key whose user is not an administrator tests as connected with a standard user's access.
    /// True: the dashboard labels the entry as a standard user and names that user.
    /// False: a standard user's key would be shown as an administrator's.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_StandardUser_ReportsStandardAccess()
    {
        var server = new ClientStubServer()
            .OnJson("/System/Info", "{\"ServerName\":\"peer\",\"Id\":\"abc\"}")
            .OnJson("/Users/Me", "{\"Id\":\"" + UserId + "\",\"Name\":\"bob\",\"Policy\":{\"IsAdministrator\":false,\"EnableContentDownloading\":false}}");
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.Equal("peer", result.ServerName);
        Assert.Equal("abc", result.ServerId);
        Assert.False(result.IsAdministrator);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal("bob", result.UserName);
        Assert.False(result.CanDownload);
        Assert.Equal("Connection successful, with a standard user's access", result.Message);
    }

    /// <summary>
    /// A key with no user behind it, which the current user route refuses, tests as an administrator that may download.
    /// True: an API Key made in the dashboard is shown with full access.
    /// False: every API Key would be labelled a standard user and its sync types limited.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_ApiKeyWithoutUser_ReportsAdministrator()
    {
        var server = new ClientStubServer()
            .OnJson("/System/Info", "{\"ServerName\":\"peer\",\"Id\":\"abc\"}")
            .OnStatus("/Users/Me", HttpStatusCode.BadRequest);
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.True(result.IsAdministrator);
        Assert.Null(result.UserId);
        Assert.True(result.CanDownload);
        Assert.Equal("Connection successful", result.Message);
    }

    /// <summary>
    /// A 403 from the system info route falls back to the public info route and reports a standard user.
    /// True: a standard user's token still names the server it reached.
    /// False: a valid standard user's token would test as a failed connection.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_ForbiddenInfo_FallsBackToPublicInfo()
    {
        var server = new ClientStubServer()
            .OnStatus("/System/Info", HttpStatusCode.Forbidden)
            .OnJson("/System/Info/Public", "{\"ServerName\":\"public-peer\",\"Id\":\"pub\"}");
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.Equal("public-peer", result.ServerName);
        Assert.Equal("pub", result.ServerId);
        Assert.False(result.IsAdministrator);
        Assert.Empty(server.To("/Users/Me"));
    }

    /// <summary>
    /// A refused key or a broken server tests as failed and never reaches the public info route.
    /// True: a wrong API Key is reported as wrong.
    /// False: any server that answers its public route would accept any key.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TestConnectionAsync_ErrorOtherThanForbidden_FailsWithoutFallback(HttpStatusCode status)
    {
        var server = new ClientStubServer()
            .OnStatus("/System/Info", status)
            .OnJson("/System/Info/Public", "{\"ServerName\":\"public-peer\",\"Id\":\"pub\"}");
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.StartsWith("Connection failed: ", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(result.ErrorMessage, result.Message);
        Assert.Empty(server.To("/System/Info/Public"));
    }

    /// <summary>
    /// A connection that cannot be made tests as failed with the HTTP error.
    /// True: an unreachable server shows why on the Servers tab.
    /// False: the page's request would fail with an unhandled error.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_ConnectionFails_ReportsHttpError()
    {
        var server = new ClientStubServer().OnThrow("/System/Info", new HttpRequestException("no route to host"));
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Equal("HTTP error: no route to host", result.ErrorMessage);
    }

    /// <summary>
    /// A request that timed out tests as failed with the timeout message.
    /// True: a slow or silent server is named as such.
    /// False: a timeout would surface as a cancelled request the page cannot explain.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_Timeout_ReportsTimedOut()
    {
        var server = new ClientStubServer().OnThrow("/System/Info", new TaskCanceledException("timeout", new TimeoutException()));
        using var client = server.Client();

        var result = await client.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Equal("Connection timed out - server may be unreachable or slow to respond", result.ErrorMessage);
    }

    /// <summary>
    /// A cancelled test throws and returns no result.
    /// True: closing the page stops the test without blaming the API Key.
    /// False: a cancelled test would be reported as a failed connection.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_Cancelled_Throws()
    {
        using var client = new ClientStubServer().Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TestConnectionAsync(cancelled.Token));
    }

    /// <summary>
    /// A disposed client tests as failed and sends nothing.
    /// True: a call after disposal is reported and never reaches the source.
    /// False: a disposed client would keep calling the source.
    /// </summary>
    [Fact]
    public async Task TestConnectionAsync_AfterDispose_FailsWithoutARequest()
    {
        var server = new ClientStubServer().OnJson("/System/Info", "{\"ServerName\":\"peer\",\"Id\":\"abc\"}");
        var client = server.Client();
        client.Dispose();
        client.Dispose();

        var result = await client.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Sign in uses the guarded client that fits the private network setting.
    /// True: an entry that disallows private addresses is checked at connect time.
    /// False: a sign in could reach a private address the entry forbids.
    /// </summary>
    [Theory]
    [InlineData(true, SourceServerClient.HttpClientName)]
    [InlineData(false, SourceServerClient.PublicHttpClientName)]
    public async Task AuthenticateAsync_PrivateNetworkSetting_PicksTheNamedClient(bool allowPrivateNetwork, string expectedName)
    {
        var server = new ClientStubServer().OnJson("/Users/AuthenticateByName", "{\"AccessToken\":\"t\"}");
        var (factory, stub) = Factory(server);

        await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "u", "p", "this", "1.0", allowPrivateNetwork);

        Assert.Equal(expectedName, Assert.Single(stub.Calls).Args[0]);
    }

    /// <summary>
    /// A successful sign in returns the token, the server id, and the user's access.
    /// True: the entry stores the token and labels the access it grants.
    /// False: a sign in would save no key or the wrong access level.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_Success_ReturnsTokenAndAccess()
    {
        var server = new ClientStubServer().OnJson(
            "/Users/AuthenticateByName",
            "{\"AccessToken\":\"tok\",\"ServerId\":\"srv\",\"User\":{\"Name\":\"Alice\",\"Id\":\"" + UserId + "\",\"Policy\":{\"IsAdministrator\":true,\"EnableContentDownloading\":false}}}");
        var (factory, _) = Factory(server);

        var result = await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url + "/", "alice", "pw", "this", "1.0", true);

        Assert.True(result.Success);
        Assert.Equal("tok", result.AccessToken);
        Assert.Equal("srv", result.ServerId);
        Assert.Equal("Alice", result.Username);
        Assert.Equal(UserId, result.UserId);
        Assert.True(result.IsAdministrator);
        Assert.False(result.CanDownload);
        var sent = Assert.Single(server.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/Users/AuthenticateByName", sent.Path);
        Assert.Equal("{\"Username\":\"alice\",\"Pw\":\"pw\"}", sent.Body);
    }

    /// <summary>
    /// An answer without a user keeps the typed username and leaves the access unknown.
    /// True: the entry still names who signed in.
    /// False: a sparse answer would save an entry with no user name, or invent an access level.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_AnswerWithoutUser_KeepsTheTypedName()
    {
        var server = new ClientStubServer().OnJson("/Users/AuthenticateByName", "{\"AccessToken\":\"tok\"}");
        var (factory, _) = Factory(server);

        var result = await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "alice", "pw", "this", "1.0", true);

        Assert.True(result.Success);
        Assert.Equal("alice", result.Username);
        Assert.Null(result.UserId);
        Assert.Null(result.IsAdministrator);
        Assert.Null(result.CanDownload);
        Assert.Null(result.ServerId);
    }

    /// <summary>
    /// A refused sign in fails, with a plain message for a wrong password and the status for anything else.
    /// True: the page tells a wrong password from a broken server.
    /// False: a refusal would read as signed in, or every refusal would blame the password.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Invalid username or password")]
    [InlineData(HttpStatusCode.InternalServerError, "Authentication failed: InternalServerError")]
    public async Task AuthenticateAsync_Refused_FailsWithTheReason(HttpStatusCode status, string expected)
    {
        var server = new ClientStubServer().OnStatus("/Users/AuthenticateByName", status);
        var (factory, _) = Factory(server);

        var result = await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "u", "p", "this", "1.0", true);

        Assert.False(result.Success);
        Assert.Null(result.AccessToken);
        Assert.Equal(expected, result.Message);
    }

    /// <summary>
    /// An answer that is not the expected JSON fails the sign in.
    /// True: a proxy's HTML page is reported as a failed sign in.
    /// False: the page's request would fail with an unhandled parse error.
    /// </summary>
    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{}")]
    public async Task AuthenticateAsync_UnreadableAnswer_Fails(string body)
    {
        var server = new ClientStubServer().OnJson("/Users/AuthenticateByName", body);
        var (factory, _) = Factory(server);

        var result = await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "u", "p", "this", "1.0", true);

        Assert.False(result.Success);
        Assert.StartsWith("Authentication failed: ", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sign in that cannot connect or times out fails with a connection message.
    /// True: an unreachable server is named as unreachable.
    /// False: a network fault would escape as an unhandled error.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_ConnectionFaults_FailWithAMessage()
    {
        var refused = new ClientStubServer().OnThrow("/Users/AuthenticateByName", new HttpRequestException("refused"));
        var slow = new ClientStubServer().OnThrow("/Users/AuthenticateByName", new TaskCanceledException("t", new TimeoutException()));

        var refusedResult = await SourceServerClient.AuthenticateAsync(Factory(refused).Factory, ClientStubServer.Url, "u", "p", "this", "1.0", true);
        var slowResult = await SourceServerClient.AuthenticateAsync(Factory(slow).Factory, ClientStubServer.Url, "u", "p", "this", "1.0", true);

        Assert.False(refusedResult.Success);
        Assert.Equal("Connection error: refused", refusedResult.Message);
        Assert.False(slowResult.Success);
        Assert.Equal("Connection timed out - server may be unreachable", slowResult.Message);
    }

    /// <summary>
    /// A cancelled sign in throws.
    /// True: closing the dialog stops the sign in.
    /// False: a cancelled sign in would be shown as a wrong password.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_Cancelled_Throws()
    {
        var (factory, _) = Factory(new ClientStubServer());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "u", "p", "this", "1.0", true, cancellationToken: cancelled.Token));
    }

    /// <summary>
    /// The sign in header carries this server's name reduced to what a header can hold, and no token.
    /// True: a server named with an emoji or a quote can still sign in.
    /// False: the request would fail before it is sent.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_UnsafeServerName_IsSentHeaderSafe()
    {
        var server = new ClientStubServer().OnJson("/Users/AuthenticateByName", "{\"AccessToken\":\"t\"}");
        var (factory, _) = Factory(server);

        await SourceServerClient.AuthenticateAsync(factory, ClientStubServer.Url, "u", "p", "My \"Den\" \U0001F920", "9.9", true);

        var header = Assert.Single(server.Seen).Authorization!;
        Assert.StartsWith("MediaBrowser Client=\"Server Sync\", Device=\"My Den\", DeviceId=\"serversync-plugin-", header, StringComparison.Ordinal);
        Assert.EndsWith(", Version=\"9.9\"", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Token=", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sign in refuses a missing client factory or Server URL.
    /// True: a bad call fails at once with the argument's name.
    /// False: it would fail later with a null reference.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsync_MissingArgument_Throws()
    {
        var (factory, _) = Factory(new ClientStubServer());

        await Assert.ThrowsAsync<ArgumentNullException>(() => SourceServerClient.AuthenticateAsync(null!, ClientStubServer.Url, "u", "p", "n", "v", true));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SourceServerClient.AuthenticateAsync(factory, null!, "u", "p", "n", "v", true));
    }

    /// <summary>
    /// The administrator route's libraries are returned as they are.
    /// True: the mapping picker lists each library with its folders.
    /// False: an administrator's key would list nothing to map.
    /// </summary>
    [Fact]
    public async Task GetLibrariesAsync_AdministratorRoute_ReturnsTheLibraries()
    {
        var server = new ClientStubServer().OnJson("/Library/VirtualFolders", "[{\"Name\":\"Movies\",\"ItemId\":\"lib1\",\"Locations\":[\"/media/movies\"]}]");
        using var client = server.Client();

        var libraries = await client.GetLibrariesAsync(UserId);

        var library = Assert.Single(libraries);
        Assert.Equal("Movies", library.Name);
        Assert.Equal("lib1", library.ItemId);
        Assert.Equal(new[] { "/media/movies" }, library.Locations);
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// A refused administrator route falls back to the user's views, leaving out views that are not media libraries or lack an id or a name.
    /// True: a standard user's key lists the libraries that user can see.
    /// False: a standard user could map nothing, or could map Playlists and Live TV as libraries.
    /// </summary>
    [Fact]
    public async Task GetLibrariesAsync_RouteRefused_FallsBackToTheUsersViews()
    {
        var server = new ClientStubServer()
            .OnStatus("/Library/VirtualFolders", HttpStatusCode.Forbidden)
            .OnJson(
                "/Users/" + UserId + "/Views",
                "{\"Items\":[{\"Id\":\"a\",\"Name\":\"Movies\",\"CollectionType\":\"movies\"},{\"Id\":\"b\",\"Name\":\"Playlists\",\"CollectionType\":\"playlists\"},"
                + "{\"Id\":\"c\",\"Name\":\"TV\",\"CollectionType\":\"LiveTv\"},{\"Id\":\"d\",\"Name\":\"Sets\",\"CollectionType\":\"boxsets\"},"
                + "{\"Id\":\"e\",\"Name\":\"Mixed\"},{\"Id\":\"\",\"Name\":\"NoId\"},{\"Id\":\"g\"}]}");
        using var client = server.Client();

        var libraries = await client.GetLibrariesAsync(UserId);

        Assert.Equal(new[] { "Movies", "Mixed" }, libraries.Select(l => l.Name));
        Assert.Equal(new[] { "a", "e" }, libraries.Select(l => l.ItemId));
        Assert.All(libraries, l => Assert.Empty(l.Locations!));
        Assert.Contains("Token=\"key\"", Assert.Single(server.To("/Users/" + UserId + "/Views")).Authorization, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a user to fall back to, or when the fallback also fails, no libraries are returned.
    /// True: the picker shows an empty list.
    /// False: the settings page would fail to load.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(UserId)]
    public async Task GetLibrariesAsync_NothingAnswers_ReturnsNone(string? authenticatedUserId)
    {
        var server = new ClientStubServer().OnStatus("/Library/VirtualFolders", HttpStatusCode.Forbidden);
        using var client = server.Client();

        var libraries = await client.GetLibrariesAsync(authenticatedUserId);

        Assert.Empty(libraries);
        Assert.Equal(string.IsNullOrEmpty(authenticatedUserId) ? 1 : 2, server.Seen.Count);
    }

    /// <summary>
    /// A cancelled library listing throws.
    /// True: leaving the page stops the listing.
    /// False: a cancelled listing would read as a server with no libraries.
    /// </summary>
    [Fact]
    public async Task GetLibrariesAsync_Cancelled_Throws()
    {
        using var client = new ClientStubServer().Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLibrariesAsync(UserId, cancelled.Token));
    }

    /// <summary>
    /// The administrator route's users are returned as they are.
    /// True: the user mapping picker lists every user of the source.
    /// False: an administrator's key would list no users to map.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_AdministratorRoute_ReturnsTheUsers()
    {
        var server = new ClientStubServer().OnJson("/Users", "[{\"Id\":\"" + UserId + "\",\"Name\":\"alice\"},{\"Id\":\"11111111111111111111111111111111\",\"Name\":\"bob\"}]");
        using var client = server.Client();

        var users = await client.GetUsersAsync();

        Assert.Equal(new[] { "alice", "bob" }, users.Select(u => u.Name));
    }

    /// <summary>
    /// A refused administrator route falls back to the signed in user alone.
    /// True: a standard user's key can still map that one user.
    /// False: a standard user's key would list no user at all.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_RouteRefused_FallsBackToTheOneUser()
    {
        var server = new ClientStubServer()
            .OnStatus("/Users", HttpStatusCode.Forbidden)
            .OnJson("/Users/" + UserId, "{\"Id\":\"" + UserId + "\",\"Name\":\"alice\"}");
        using var client = server.Client();

        var users = await client.GetUsersAsync(UserId);

        var user = Assert.Single(users);
        Assert.Equal(Guid.Parse(UserId), user.Id);
        Assert.Equal("alice", user.Name);
    }

    /// <summary>
    /// A fallback that cannot name a user, has no user to ask for, or fails returns no users.
    /// True: the picker shows an empty list.
    /// False: a user with an unreadable id would be offered for mapping, or the page would fail.
    /// </summary>
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(UserId, "{\"Id\":\"not-a-guid\",\"Name\":\"alice\"}", 2)]
    [InlineData(UserId, "{\"Name\":\"alice\"}", 2)]
    [InlineData(UserId, null, 2)]
    public async Task GetUsersAsync_NoUsableFallback_ReturnsNone(string? authenticatedUserId, string? fallbackBody, int expectedRequests)
    {
        var server = new ClientStubServer().OnStatus("/Users", HttpStatusCode.Forbidden);
        if (fallbackBody is not null)
        {
            server.OnJson("/Users/" + UserId, fallbackBody);
        }

        using var client = server.Client();

        var users = await client.GetUsersAsync(authenticatedUserId);

        Assert.Empty(users);
        Assert.Equal(expectedRequests, server.Seen.Count);
    }

    /// <summary>
    /// A name with nothing a header can carry becomes the fixed client name.
    /// True: a server with no name, or one made of control characters, still sends a valid header.
    /// False: the device name would be empty or break the request.
    /// </summary>
    [Theory]
    [InlineData(null, "Server Sync")]
    [InlineData("   ", "Server Sync")]
    [InlineData("\t\r\n", "Server Sync")]
    [InlineData("  Den\u0001 ~", "Den ~")]
    public void HeaderSafe_NothingPrintable_FallsBack(string? name, string expected)
    {
        var safe = SourceServerClient.HeaderSafe(name);

        Assert.Equal(expected, safe);
    }

    private static (IHttpClientFactory Factory, InterfaceStub Stub) Factory(ClientStubServer server)
    {
        var (factory, stub) = InterfaceStub.Create<IHttpClientFactory>();
        stub.Answer(nameof(IHttpClientFactory.CreateClient), _ => new HttpClient(server, disposeHandler: false));
        return (factory, stub);
    }
}
