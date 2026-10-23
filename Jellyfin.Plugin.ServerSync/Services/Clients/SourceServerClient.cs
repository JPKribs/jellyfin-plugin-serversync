using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Client for communicating with the source Jellyfin server using the official SDK.
/// The HttpClient is externally owned (via IHttpClientFactory) and must NOT be disposed by this class.
/// </summary>
public partial class SourceServerClient : IDisposable
{
    // Client identification constants
    private const string DefaultClientName = "Server Sync";
    private static readonly string DefaultDeviceId = "serversync-plugin-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// Named HttpClient identifier for IHttpClientFactory.
    /// </summary>
    public const string HttpClientName = "ServerSyncSource";

    /// <summary>
    /// Named HttpClient for entries that disallow private networks. Its handler classifies every address a
    /// name resolves to at connect time, so a name cannot reach what an address could not.
    /// </summary>
    public const string PublicHttpClientName = "ServerSyncSourcePublic";

    private readonly ILogger<SourceServerClient> _logger;
    private readonly string _serverUrl;
    private readonly string _apiKey;
    private readonly string _localServerName;
    private readonly string _pluginVersion;
    private readonly HttpClient _httpClient;
    private readonly JellyfinSdkSettings _sdkSettings;
    private readonly object _apiClientLock = new();
    private JellyfinApiClient? _apiClient;
    private volatile bool _disposed;

    /// <summary>
    /// Gets or sets the pairing with this peer, when the peer is a configured server entry. It supplies
    /// the secret presented on requests that name this server as the sender, and pairs again when the
    /// peer answers that the pairing is missing.
    /// </summary>
    public PeerPairing? Pairing { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceServerClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="httpClient">HttpClient instance (externally owned, e.g. from IHttpClientFactory).</param>
    /// <param name="serverUrl">Source server URL.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <param name="localServerName">Local server name for client identification.</param>
    /// <param name="pluginVersion">Plugin version string for client identification.</param>
    public SourceServerClient(
        ILogger<SourceServerClient> logger,
        HttpClient httpClient,
        string serverUrl,
        string apiKey,
        string localServerName,
        string pluginVersion)
    {
        _logger = logger;
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey = apiKey;
        _localServerName = localServerName;
        _pluginVersion = pluginVersion;
        _httpClient = httpClient;

        _sdkSettings = new JellyfinSdkSettings();
        _sdkSettings.SetServerUrl(_serverUrl);
        _sdkSettings.Initialize(
            clientName: DefaultClientName,
            clientVersion: _pluginVersion,
            deviceName: _localServerName,
            deviceId: DefaultDeviceId);
        _sdkSettings.SetAccessToken(_apiKey);
    }

    /// <summary>
    /// Tests connection to the source server and returns server info.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test result with server info.</returns>
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();

            // Try the authenticated /System/Info endpoint first (validates token and gets full info).
            // If the token belongs to a non-admin user, this may return 403, fall back to /System/Info/Public.
            try
            {
                var info = await client.System.Info.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var access = await ReadAccessAsync(client, cancellationToken).ConfigureAwait(false);
                return new ConnectionTestResult
                {
                    Success = true,
                    ServerName = info?.ServerName,
                    ServerId = info?.Id,
                    IsAdministrator = access.IsAdministrator,
                    UserId = access.UserId,
                    UserName = access.UserName,
                    CanDownload = access.CanDownload,
                    Message = access.IsAdministrator ? "Connection successful" : "Connection successful, with a standard user's access"
                };
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                // 403 is the only legitimate fallback case (valid token, non-
                // admin user). /System/Info/Public is anonymous, so falling
                // back on 401 or transport errors would report "connection
                // successful" for a revoked/expired API key, the user sees a
                // green check while every sync silently fails.
                _logger.LogDebug(ex, "Authenticated /System/Info returned 403 (non-admin token), falling back to /System/Info/Public");
            }

            // Fallback: use /System/Info/Public (available to all authenticated users)
            var publicInfo = await client.System.Info.Public.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ConnectionTestResult
            {
                Success = true,
                ServerName = publicInfo?.ServerName,
                ServerId = publicInfo?.Id,
                IsAdministrator = false,
                Message = "Connection successful, with a standard user's access"
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            var errorMsg = "Connection timed out - server may be unreachable or slow to respond";
            _logger.LogWarning("Connection to source server timed out");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
        }
        catch (OperationCanceledException)
        {
            // Propagate rather than reporting a failed connection. Both task
            // bases treat a false result as "unreachable or credentials
            // invalid" and pin that on the dashboard until a clean run clears
            // it, so cancelling a task from Scheduled Tasks used to tell the
            // user their API key was bad. The genuine-timeout case is caught
            // by the TaskCanceledException/TimeoutException clause above.
            throw;
        }
        catch (HttpRequestException ex)
        {
            var errorMsg = $"HTTP error: {ex.Message}";
            _logger.LogError(ex, "HTTP error connecting to source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
        }
        catch (Exception ex)
        {
            var errorMsg = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "Failed to connect to source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
        }
    }

    /// <summary>
    /// Authenticates with a Jellyfin server using username and password.
    /// Returns an access token that can be used for subsequent API calls.
    /// This is a static method that doesn't require an existing client instance.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory for creating clients.</param>
    /// <param name="serverUrl">Server URL.</param>
    /// <param name="username">Username.</param>
    /// <param name="password">Password.</param>
    /// <param name="localServerName">Local server name for client identification.</param>
    /// <param name="pluginVersion">Plugin version string for client identification.</param>
    /// <param name="allowPrivateNetwork">Whether the server may sit on a private network. When it may not, the request goes through the client that checks every resolved address at connect time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result with access token if successful.</returns>
    public static async Task<AuthenticateResponse> AuthenticateAsync(
        IHttpClientFactory httpClientFactory,
        string serverUrl,
        string username,
        string password,
        string localServerName,
        string pluginVersion,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken = default)
    {
        serverUrl = serverUrl.TrimEnd('/');

        // The URL check only classifies an address written into the URL. A name resolves at connect
        // time, so an entry that disallows private networks needs the client whose handler checks the
        // resolved address too, the same choice the client factory makes.
        var httpClient = httpClientFactory.CreateClient(allowPrivateNetwork ? HttpClientName : PublicHttpClientName);

        // Build the authentication request
        var authRequest = new
        {
            Username = username,
            Pw = password
        };

        var json = System.Text.Json.JsonSerializer.Serialize(authRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Per Jellyfin auth spec, AuthenticateByName requires client
        // identification via the Authorization header (no Token yet, we're
        // about to obtain it). Format must be the canonical
        // `MediaBrowser key="value", ...` scheme. The previous code used
        // `X-Emby-Authorization` which is deprecated and rejected when
        // `EnableLegacyAuthorization=false`. Header lives on the request,
        // not on Content (Authorization is not a content-level header).
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/Users/AuthenticateByName")
        {
            Content = content
        };
        // The device name is this server's name, which may hold characters a header cannot carry.
        var authHeader = $"Client=\"{DefaultClientName}\", Device=\"{HeaderSafe(localServerName)}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{pluginVersion}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authHeader);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new AuthenticateResponse
                {
                    Success = false,
                    Message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "Invalid username or password"
                        : $"Authentication failed: {response.StatusCode}"
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var authResponse = System.Text.Json.JsonDocument.Parse(responseJson);

            var accessToken = authResponse.RootElement.GetProperty("AccessToken").GetString();
            var serverName = authResponse.RootElement.TryGetProperty("ServerId", out var serverIdProp)
                ? serverIdProp.GetString()
                : null;

            // Get user info
            string? authenticatedUsername = null;
            string? authenticatedUserId = null;
            bool? isAdministrator = null;
            bool? canDownload = null;
            if (authResponse.RootElement.TryGetProperty("User", out var userProp))
            {
                if (userProp.TryGetProperty("Name", out var nameProp))
                {
                    authenticatedUsername = nameProp.GetString();
                }

                if (userProp.TryGetProperty("Id", out var idProp))
                {
                    authenticatedUserId = idProp.GetString();
                }

                if (userProp.TryGetProperty("Policy", out var policyProp))
                {
                    if (policyProp.TryGetProperty("IsAdministrator", out var adminProp)
                        && adminProp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    {
                        isAdministrator = adminProp.GetBoolean();
                    }

                    if (policyProp.TryGetProperty("EnableContentDownloading", out var downloadProp)
                        && downloadProp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    {
                        canDownload = downloadProp.GetBoolean();
                    }
                }
            }

            return new AuthenticateResponse
            {
                Success = true,
                AccessToken = accessToken,
                Username = authenticatedUsername ?? username,
                UserId = authenticatedUserId,
                IsAdministrator = isAdministrator,
                CanDownload = canDownload,
                ServerId = serverName
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = "Connection timed out - server may be unreachable"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = $"Connection error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = $"Authentication failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets all libraries from the source server.
    /// Tries the admin /Library/VirtualFolders endpoint first.
    /// If that fails (e.g. non-admin user token), falls back to /Users/{userId}/Views.
    /// </summary>
    /// <param name="authenticatedUserId">Optional authenticated user ID for fallback to user-scoped endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of virtual folder info.</returns>
    public async Task<List<VirtualFolderInfo>> GetLibrariesAsync(string? authenticatedUserId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();

            // Try admin endpoint first (returns libraries with file-system Locations)
            try
            {
                var folders = await client.Library.VirtualFolders.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return folders ?? new List<VirtualFolderInfo>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Admin /Library/VirtualFolders failed, attempting user-scoped fallback");
            }

            // Fallback: use /Users/{userId}/Views (available to non-admin users)
            if (!string.IsNullOrEmpty(authenticatedUserId))
            {
                return await GetUserViewsAsLibrariesAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Cannot fall back to user views, no authenticated user ID available");
            return new List<VirtualFolderInfo>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get libraries from source server");
            return new List<VirtualFolderInfo>();
        }
    }

    /// <summary>
    /// Gets user-accessible libraries via /Users/{userId}/Views and maps them to VirtualFolderInfo.
    /// This endpoint works for non-admin users but does not include file-system Locations.
    /// </summary>
    private async Task<List<VirtualFolderInfo>> GetUserViewsAsLibrariesAsync(string userId, CancellationToken cancellationToken)
    {
        var url = $"{_serverUrl}/Users/{Uri.EscapeDataString(userId)}/Views";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var libraries = new List<VirtualFolderInfo>();

        if (doc.RootElement.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var collectionType = item.TryGetProperty("CollectionType", out var ctProp) ? ctProp.GetString() : null;

                // Only include media libraries (skip non-library views like playlists, live TV)
                if (string.Equals(collectionType, "playlists", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(collectionType, "livetv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(collectionType, "boxsets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = item.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
                var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    libraries.Add(new VirtualFolderInfo
                    {
                        ItemId = id,
                        Name = name,
                        Locations = new List<string>()
                    });
                }
            }
        }

        _logger.LogInformation("Fetched {Count} libraries via user views fallback for user {UserId}", libraries.Count, FormatUtilities.SanitizeForLog(userId));
        return libraries;
    }

    /// <summary>
    /// Gets all users from the source server.
    /// Falls back to fetching only the authenticated user if the admin endpoint is forbidden.
    /// </summary>
    /// <param name="authenticatedUserId">Optional authenticated user ID for non-admin fallback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user DTOs.</returns>
    public async Task<List<UserDto>> GetUsersAsync(string? authenticatedUserId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                var client = GetApiClient();
                var users = await client.Users.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return users ?? new List<UserDto>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Admin /Users endpoint failed, attempting user-scoped fallback");
            }

            // Fallback: fetch just the authenticated user via /Users/{userId}
            if (!string.IsNullOrEmpty(authenticatedUserId))
            {
                return await GetSingleUserAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Cannot fall back to single user, no authenticated user ID available");
            return new List<UserDto>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get users from source server");
            return new List<UserDto>();
        }
    }

    /// <summary>
    /// Gets a single user by ID via /Users/{userId}.
    /// This endpoint is accessible to the authenticated user themselves.
    /// </summary>
    private async Task<List<UserDto>> GetSingleUserAsync(string userId, CancellationToken cancellationToken)
    {
        var url = $"{_serverUrl}/Users/{Uri.EscapeDataString(userId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var root = doc.RootElement;
        var id = root.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
        var name = root.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;

        if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out var parsedId))
        {
            _logger.LogInformation("Fetched single user via fallback: {UserName} ({UserId})", name, id);
            return new List<UserDto>
            {
                new UserDto
                {
                    Id = parsedId,
                    Name = name
                }
            };
        }

        _logger.LogWarning("Failed to parse user from /Users/{UserId} response", FormatUtilities.SanitizeForLog(userId));
        return new List<UserDto>();
    }

    /// <summary>
    /// Disposes a request/response without letting a disposal fault mask the
    /// original outcome, and without stopping the next disposal from running.
    /// </summary>
    private static void DisposeQuiet(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Already disposed or shutting down, nothing useful to do.
        }
    }

    /// <summary>
    /// Adds the canonical Jellyfin <c>Authorization: MediaBrowser ...</c>
    /// header to a request. Format per Jellyfin's auth spec:
    /// <c>MediaBrowser key="value", key2="value2"</c>. Per Jellyfin docs,
    /// the Authorization header is the only non-deprecated method for
    /// transmitting credentials. <c>X-Emby-Token</c>, <c>X-MediaBrowser-Token</c>,
    /// <c>X-Emby-Authorization</c>, and the <c>api_key</c> query parameter
    /// are all deprecated and may be rejected by Jellyfin servers with
    /// <c>EnableLegacyAuthorization=false</c>.
    /// </summary>
    /// <param name="request">HTTP request message.</param>
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        // The parameter passed to AuthenticationHeaderValue must NOT start
        // with the scheme name. `AuthenticationHeaderValue("MediaBrowser", x)`
        // emits `Authorization: MediaBrowser <x>`. Earlier code prefixed
        // "MediaBrowser " into the parameter too, yielding a double-scheme
        // header that some Jellyfin endpoints (notably Download) reject
        // with 400 Bad Request.
        // The device name is this server's name, which may hold characters a header cannot carry.
        var authValue = $"Client=\"{DefaultClientName}\", Device=\"{HeaderSafe(_localServerName)}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{_pluginVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

    /// <summary>
    /// Reduces a name to what an HTTP header value can carry: printable ASCII without quotes or
    /// backslashes. A server named with emoji otherwise makes every hand built request fail with
    /// "request headers must contain only ASCII characters", while the SDK's own requests go through.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The safe name, or a fixed fallback when nothing printable is left.</returns>
    public static string HeaderSafe(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return DefaultClientName;
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Where(ch => ch >= ' ' && ch <= '~' && ch != '"' && ch != '\\'))
        {
            builder.Append(c);
        }

        var safe = builder.ToString().Trim();
        return safe.Length == 0 ? DefaultClientName : safe;
    }

    // Any signed in user may read the system info, so that call says nothing about elevation. The
    // current user's own policy does. An API key has no user behind it and the call fails, and an API
    // key is always elevated, so a failure there means administrator. Callers use the answer for the
    // access label and to explain a limited link, while the peer's own whitelist still decides
    // access, so guessing administrator on failure grants nothing. A failure that is not the
    // expected API key answer is logged so a wrong label can be traced.
    private async Task<(bool IsAdministrator, string? UserId, string? UserName, bool? CanDownload)> ReadAccessAsync(JellyfinApiClient client, CancellationToken cancellationToken)
    {
        try
        {
            var me = await client.Users.Me.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (me is null)
            {
                return (true, null, null, null);
            }

            return (
                me.Policy?.IsAdministrator ?? true,
                me.Id?.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                me.Name,
                me.Policy?.EnableContentDownloading);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is >= 400 and < 500)
        {
            // An API key has no user, so the server refuses this call. That is the expected answer for
            // an API key and says the token is elevated.
            _logger.LogDebug(ex, "Reading the current user returned {Status}, treating the token as an API key with administrator rights", ex.ResponseStatusCode);
            return (true, null, null, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the current user's policy, reporting administrator access without confirming it: {Reason}", ex.Message);
            return (true, null, null, null);
        }
    }

    /// <summary>
    /// Returns the API client, creating it lazily if needed.
    /// </summary>
    /// <returns>Jellyfin API client instance.</returns>
    private JellyfinApiClient GetApiClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: client already created
        var client = _apiClient;
        if (client != null)
        {
            return client;
        }

        lock (_apiClientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_apiClient == null)
            {
                var authProvider = new JellyfinAuthenticationProvider(_sdkSettings);
                var adapter = new JellyfinRequestAdapter(authProvider, _sdkSettings, _httpClient);
                _apiClient = new JellyfinApiClient(adapter);
            }

            return _apiClient;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            lock (_apiClientLock)
            {
                if (!_disposed)
                {
                    _disposed = true;

                    // Do NOT dispose _httpClient, it is externally owned (from IHttpClientFactory).
                    // Dispose the API client wrapper if it was created.
                    (_apiClient as IDisposable)?.Dispose();
                    _apiClient = null;
                }
            }
        }
    }

    /// <summary>
    /// A stream wrapper that disposes the underlying HttpResponseMessage and HttpRequestMessage when the stream is closed.
    /// This ensures the HTTP connection is properly released when the caller is done reading.
    /// </summary>
    private sealed class ResponseDisposingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage? _request;

        public ResponseDisposingStream(Stream inner, HttpResponseMessage response, HttpRequestMessage? request = null)
        {
            _inner = inner;
            _response = response;
            _request = request;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
                _request?.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            _request?.Dispose();

            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Wrap the inner read with a cancellation registration that
            // forcibly disposes the response. Without this, a hung server can
            // keep an inner HttpConnection read blocked past cancellation , 
            // HttpClient.Timeout doesn't apply once response headers are in.
            using var registration = cancellationToken.Register(static state => DisposeQuiet((HttpResponseMessage)state!), _response);
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(static state => DisposeQuiet((HttpResponseMessage)state!), _response);
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        private static void DisposeQuiet(HttpResponseMessage response)
        {
            try
            {
                response.Dispose();
            }
            catch
            {
                // Already disposed or in shutdown, nothing useful to do here.
            }
        }
    }
}
