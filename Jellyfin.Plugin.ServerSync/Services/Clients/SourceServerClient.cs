using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Calls one source server through the Jellyfin SDK. The <see cref="HttpClient"/> belongs to the factory that
/// made it and is never disposed here.
/// </summary>
public partial class SourceServerClient : IDisposable
{
    private const string DefaultClientName = "Server Sync";
    private static readonly string DefaultDeviceId = "serversync-plugin-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// The named <see cref="HttpClient"/> for server entries that allow private network addresses.
    /// </summary>
    public const string HttpClientName = "ServerSyncSource";

    /// <summary>
    /// The named <see cref="HttpClient"/> for server entries that disallow private network addresses. Its
    /// handler checks every address a name resolves to at connect time.
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
    /// Gets or sets the pairing with this peer, when the peer is a saved server entry. It supplies the
    /// secret sent with peer requests and pairs again when the peer reports the pairing missing.
    /// </summary>
    public PeerPairing? Pairing { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceServerClient"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="httpClient">Sends every request. The caller owns it.</param>
    /// <param name="serverUrl">The Server URL of the source.</param>
    /// <param name="apiKey">The API Key sent with every request.</param>
    /// <param name="localServerName">This server's name, sent as the device name.</param>
    /// <param name="pluginVersion">The plugin version, sent as the client version.</param>
    public SourceServerClient(
        ILogger<SourceServerClient> logger,
        HttpClient httpClient,
        string serverUrl,
        string apiKey,
        string localServerName,
        string pluginVersion)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(serverUrl);

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
    /// Calls the source server and reports who it is and what access the API Key has.
    /// </summary>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>The outcome, with the server's name and id on success, and its message in US English.</returns>
    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        => TestConnectionAsync(null, cancellationToken);

    /// <summary>
    /// Calls the source server and reports who it is and what access the API Key has, with the message in a language.
    /// </summary>
    /// <param name="language">The language of the message, or <c>null</c> for US English.</param>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>The outcome, with the server's name and id on success.</returns>
    public async Task<ConnectionTestResult> TestConnectionAsync(string? language, CancellationToken cancellationToken)
    {
        try
        {
            var client = GetApiClient();
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
                    Message = BaseTranslations.Get(access.IsAdministrator ? TranslationKeys.MessageConnectionOk : TranslationKeys.MessageConnectionStandardUser, language)
                };
            }
            // Only 403 proves a valid API Key, so nothing else may fall back.
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                _logger.LogDebug(ex, "Authenticated /System/Info returned 403 (non-admin token), falling back to /System/Info/Public");
            }

            var publicInfo = await client.System.Info.Public.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ConnectionTestResult
            {
                Success = true,
                ServerName = publicInfo?.ServerName,
                ServerId = publicInfo?.Id,
                IsAdministrator = false,
                Message = BaseTranslations.Get(TranslationKeys.MessageConnectionStandardUser, language)
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            var errorMsg = BaseTranslations.Get(TranslationKeys.ErrorConnectionTimedOutSlow, language);
            _logger.LogWarning("Connection to source server timed out");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
        }
        catch (HttpRequestException ex)
        {
            var errorMsg = BaseTranslations.Format(TranslationKeys.ErrorHttp, language, ex.Message);
            _logger.LogError(ex, "HTTP error connecting to source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
        }
        // A cancelled test propagates, since a failed result would blame the API Key.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorMsg = BaseTranslations.Format(TranslationKeys.ErrorConnectionFailed, language, ex.Message);
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
    /// Signs in to a Jellyfin server with a username and password to obtain an access token.
    /// </summary>
    /// <param name="httpClientFactory">Builds the named client that carries the SSRF guard.</param>
    /// <param name="serverUrl">The Server URL to sign in to.</param>
    /// <param name="username">The user to sign in as.</param>
    /// <param name="password">That user's password.</param>
    /// <param name="localServerName">This server's name, sent as the device name.</param>
    /// <param name="pluginVersion">The plugin version, sent as the client version.</param>
    /// <param name="allowPrivateNetwork">Whether the server may use a private network address. When it may not, every resolved address is checked at connect time.</param>
    /// <param name="language">The language of the message, or <c>null</c> for US English.</param>
    /// <param name="cancellationToken">Cancels the sign in.</param>
    /// <returns>The outcome, with the access token on success.</returns>
    public static async Task<AuthenticateResponse> AuthenticateAsync(
        IHttpClientFactory httpClientFactory,
        string serverUrl,
        string username,
        string password,
        string localServerName,
        string pluginVersion,
        bool allowPrivateNetwork,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(serverUrl);

        serverUrl = serverUrl.TrimEnd('/');
        var httpClient = httpClientFactory.CreateClient(allowPrivateNetwork ? HttpClientName : PublicHttpClientName);

        var authRequest = new
        {
            Username = username,
            Pw = password
        };

        var json = System.Text.Json.JsonSerializer.Serialize(authRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/Users/AuthenticateByName")
        {
            Content = content
        };
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
                        ? BaseTranslations.Get(TranslationKeys.ErrorInvalidCredentials, language)
                        : BaseTranslations.Format(TranslationKeys.ErrorAuthenticationFailedWith, language, response.StatusCode)
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var authResponse = System.Text.Json.JsonDocument.Parse(responseJson);

            var accessToken = authResponse.RootElement.GetProperty("AccessToken").GetString();
            var serverId = authResponse.RootElement.TryGetProperty("ServerId", out var serverIdProp)
                ? serverIdProp.GetString()
                : null;

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
                ServerId = serverId
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = BaseTranslations.Get(TranslationKeys.ErrorConnectionTimedOut, language)
            };
        }
        catch (HttpRequestException ex)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = BaseTranslations.Format(TranslationKeys.ErrorConnectionError, language, ex.Message)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AuthenticateResponse
            {
                Success = false,
                Message = BaseTranslations.Format(TranslationKeys.ErrorAuthenticationFailedWith, language, ex.Message)
            };
        }
    }

    /// <summary>
    /// Lists the source server's libraries, falling back to one user's views when the administrator route refuses.
    /// </summary>
    /// <param name="authenticatedUserId">The source user whose views are the fallback, or null for no fallback.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The libraries, or an empty list when they could not be read.</returns>
    public async Task<List<VirtualFolderInfo>> GetLibrariesAsync(string? authenticatedUserId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            try
            {
                var folders = await client.Library.VirtualFolders.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return folders ?? new List<VirtualFolderInfo>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Admin /Library/VirtualFolders failed, attempting user-scoped fallback");
            }

            if (!string.IsNullOrEmpty(authenticatedUserId))
            {
                return await GetUserViewsAsLibrariesAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Cannot fall back to user views, no authenticated user ID available");
            return new List<VirtualFolderInfo>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get libraries from source server");
            return new List<VirtualFolderInfo>();
        }
    }

    /// <summary>
    /// Lists the libraries one user can see, which carry no folder locations.
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

                // These views are not media libraries.
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

        _logger.LogDebug("Fetched {Count} libraries via user views fallback for user {UserId}", libraries.Count, FormatUtilities.SanitizeForLog(userId));
        return libraries;
    }

    /// <summary>
    /// Lists the source server's users, falling back to the signed in user alone when the administrator route refuses.
    /// </summary>
    /// <param name="authenticatedUserId">The source user to fall back to, or null for no fallback.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The users, or an empty list when they could not be read.</returns>
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Admin /Users endpoint failed, attempting user-scoped fallback");
            }

            if (!string.IsNullOrEmpty(authenticatedUserId))
            {
                return await GetSingleUserAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("Cannot fall back to single user, no authenticated user ID available");
            return new List<UserDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get users from source server");
            return new List<UserDto>();
        }
    }

    /// <summary>
    /// Reads one user by id, a route a user may call for themselves.
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
            _logger.LogDebug("Fetched single user via fallback: {UserName} ({UserId})", FormatUtilities.SanitizeForLog(name), FormatUtilities.SanitizeForLog(id));
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
    /// Disposes a request or response without letting a disposal fault hide the original outcome.
    /// </summary>
    private static void DisposeQuiet(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception)
        {
            // Runs in cleanup and cancellation callbacks, where no disposal fault may escape.
        }
    }

    /// <summary>
    /// Adds the <c>Authorization: MediaBrowser</c> header, the only credential form Jellyfin does not deprecate.
    /// The header value must not repeat the scheme name, which some routes refuse with 400.
    /// </summary>
    /// <param name="request">The request that receives the header.</param>
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        var authValue = $"Client=\"{DefaultClientName}\", Device=\"{HeaderSafe(_localServerName)}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{_pluginVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

    /// <summary>
    /// Reduces a name to printable ASCII without quotes or backslashes, which is all an HTTP header value can
    /// carry. A request built with any other character fails before it is sent.
    /// </summary>
    /// <param name="name">The server name to send as the device name.</param>
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

    /// <summary>
    /// Reads the access behind the token from the current user's policy. A failure reports administrator,
    /// since an API Key has no user, and the answer only labels access and grants none.
    /// </summary>
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
        // An API Key has no user, so a refusal here means the token is elevated.
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is >= 400 and < 500)
        {
            _logger.LogDebug(ex, "Reading the current user returned {Status}, treating the token as an API key with administrator rights", ex.ResponseStatusCode);
            return (true, null, null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the current user's policy, reporting administrator access without confirming it");
            return (true, null, null, null);
        }
    }

    /// <summary>
    /// Returns the SDK client, creating it on first use.
    /// </summary>
    /// <returns>The SDK client bound to this source.</returns>
    private JellyfinApiClient GetApiClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the SDK client once. The <see cref="HttpClient"/> is left to the factory that owns it.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            lock (_apiClientLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    (_apiClient as IDisposable)?.Dispose();
                    _apiClient = null;
                }
            }
        }
    }

    /// <summary>
    /// Wraps a response body so closing the stream also releases its response and request. A cancelled
    /// read disposes the response, since a hung server can otherwise hold the read past cancellation.
    /// </summary>
    private sealed class ResponseDisposingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage? _request;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResponseDisposingStream"/> class.
        /// </summary>
        /// <param name="inner">The response body to read.</param>
        /// <param name="response">The response released with the stream.</param>
        /// <param name="request">The request released with the stream, when there is one.</param>
        public ResponseDisposingStream(Stream inner, HttpResponseMessage response, HttpRequestMessage? request = null)
        {
            _inner = inner;
            _response = response;
            _request = request;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            _request?.Dispose();

            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override bool CanRead => _inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => _inner.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => _inner.CanWrite;

        /// <inheritdoc />
        public override long Length => _inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => _inner.SetLength(value);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        /// <inheritdoc />
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(static state => DisposeQuiet((HttpResponseMessage)state!), _response);
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(static state => DisposeQuiet((HttpResponseMessage)state!), _response);
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
