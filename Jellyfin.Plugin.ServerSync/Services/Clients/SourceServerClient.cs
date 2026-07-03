using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Client for communicating with the source Jellyfin server using the official SDK.
/// The HttpClient is externally owned (via IHttpClientFactory) and must NOT be disposed by this class.
/// </summary>
public class SourceServerClient : IDisposable
{
    // Client identification constants
    private const string DefaultClientName = "Server Sync";
    private static readonly string DefaultDeviceId = "serversync-plugin-" + Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// Named HttpClient identifier for IHttpClientFactory.
    /// </summary>
    public const string HttpClientName = "ServerSyncSource";

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
            // If the token belongs to a non-admin user, this may return 403 — fall back to /System/Info/Public.
            try
            {
                var info = await client.System.Info.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                return new ConnectionTestResult
                {
                    Success = true,
                    ServerName = info?.ServerName,
                    ServerId = info?.Id,
                    Message = "Connection successful"
                };
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 403)
            {
                // 403 is the only legitimate fallback case (valid token, non-
                // admin user). /System/Info/Public is anonymous, so falling
                // back on 401 or transport errors would report "connection
                // successful" for a revoked/expired API key — the user sees a
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
                Message = "Connection successful"
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
            var errorMsg = "Connection test was cancelled";
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = errorMsg,
                Message = errorMsg
            };
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication result with access token if successful.</returns>
    public static async Task<AuthenticateResponse> AuthenticateAsync(
        IHttpClientFactory httpClientFactory,
        string serverUrl,
        string username,
        string password,
        string localServerName,
        string pluginVersion,
        CancellationToken cancellationToken = default)
    {
        serverUrl = serverUrl.TrimEnd('/');

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        // Build the authentication request
        var authRequest = new
        {
            Username = username,
            Pw = password
        };

        var json = System.Text.Json.JsonSerializer.Serialize(authRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Per Jellyfin auth spec, AuthenticateByName requires client
        // identification via the Authorization header (no Token yet — we're
        // about to obtain it). Format must be the canonical
        // `MediaBrowser key="value", ...` scheme. The previous code used
        // `X-Emby-Authorization` which is deprecated and rejected when
        // `EnableLegacyAuthorization=false`. Header lives on the request,
        // not on Content (Authorization is not a content-level header).
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/Users/AuthenticateByName")
        {
            Content = content
        };
        var authHeader = $"Client=\"{DefaultClientName}\", Device=\"{localServerName}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{pluginVersion}\"";
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
            }

            return new AuthenticateResponse
            {
                Success = true,
                AccessToken = accessToken,
                Username = authenticatedUsername ?? username,
                UserId = authenticatedUserId,
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

            _logger.LogWarning("Cannot fall back to user views — no authenticated user ID available");
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

        _logger.LogInformation("Fetched {Count} libraries via user views fallback for user {UserId}", libraries.Count, userId);
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

            _logger.LogWarning("Cannot fall back to single user — no authenticated user ID available");
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

        _logger.LogWarning("Failed to parse user from /Users/{UserId} response", userId);
        return new List<UserDto>();
    }

    /// <summary>
    /// Fetches the Content-sync leaves for a single whitelisted item: returns
    /// the item itself if it's a leaf, or walks its descendants folder-by-
    /// folder if it's a folder type. The folder walk avoids the
    /// <c>ParentId</c>+<c>Recursive=true</c> path because Jellyfin keeps the
    /// literal <c>ParentId</c> filter on recursive queries, which returns
    /// nothing for a Series since episodes carry the Season's ID, not the
    /// Series'.
    /// </summary>
    public async Task<List<BaseItemDto>> GetWhitelistedItemLeavesAsync(
        Guid whitelistedId,
        CancellationToken cancellationToken = default)
    {
        var leafTypes = new HashSet<BaseItemDto_Type?>
        {
            BaseItemDto_Type.Movie,
            BaseItemDto_Type.Episode,
            BaseItemDto_Type.Audio,
            BaseItemDto_Type.Video
        };

        var result = new List<BaseItemDto>();
        var seen = new HashSet<Guid>();

        BaseItemDto? rootItem;
        try
        {
            var client = GetApiClient();
            var rootPage = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.Ids = new Guid?[] { whitelistedId };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            rootItem = rootPage?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} on whitelist root {Id}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, whitelistedId);
            throw;
        }
        catch (Exception ex)
        {
            // Transport faults (connection reset, DNS, TLS) and deserialization
            // errors land here — Kiota only wraps HTTP status errors as
            // ApiException. Returning an empty list would make every leaf under
            // this entry look "missing from source" and schedule its files for
            // deletion, so bubble up instead.
            _logger.LogWarning(ex, "Whitelist: failed to fetch item {Id}; aborting to protect tracking rows", whitelistedId);
            throw;
        }

        if (rootItem == null || !rootItem.Id.HasValue)
        {
            _logger.LogWarning("Whitelist: source server returned no item for id {Id}", whitelistedId);
            return result;
        }

        if (leafTypes.Contains(rootItem.Type))
        {
            result.Add(rootItem);
            seen.Add(rootItem.Id.Value);
            return result;
        }

        // Folder-like (Series / Season / BoxSet / Album / Artist): walk
        // children breadth-first one folder at a time, collecting leaves.
        var queue = new Queue<Guid>();
        queue.Enqueue(rootItem.Id.Value);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentId = queue.Dequeue();

            var startIndex = 0;
            const int pageSize = 1000;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BaseItemDtoQueryResult? page;
                try
                {
                    var client = GetApiClient();
                    page = await client.Items.GetAsync(
                        config =>
                        {
                            config.QueryParameters.ParentId = parentId;
                            config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                            config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                            config.QueryParameters.StartIndex = startIndex;
                            config.QueryParameters.Limit = pageSize;
                        },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
                {
                    // Any 4xx/5xx: bubble up. `break` here would silently
                    // truncate the leaf list, which the caller can't tell
                    // apart from "this folder really has no more children" —
                    // exactly the kind of silent partial result that drives
                    // the prune massacre.
                    _logger.LogWarning(ex, "Source server {Status} listing whitelist children of {ParentId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, parentId);
                    throw;
                }
                catch (Exception ex)
                {
                    // Transport faults reach here as HttpRequestException, not
                    // ApiException. A `break` would truncate the leaf list the
                    // same way a swallowed 5xx would — bubble up instead.
                    _logger.LogWarning(ex, "Whitelist: failed to fetch children of {ParentId}; aborting to protect tracking rows", parentId);
                    throw;
                }

                if (page?.Items == null || page.Items.Count == 0)
                {
                    break;
                }

                foreach (var child in page.Items)
                {
                    if (!child.Id.HasValue || !seen.Add(child.Id.Value))
                    {
                        continue;
                    }

                    if (leafTypes.Contains(child.Type))
                    {
                        result.Add(child);
                    }
                    else
                    {
                        // Folder — descend.
                        queue.Enqueue(child.Id.Value);
                    }
                }

                if (page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value)
                {
                    break;
                }

                if (page.Items.Count < pageSize)
                {
                    break;
                }

                startIndex += page.Items.Count;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets items from a library with pagination support.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemsAsync(
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
                    // Sort by Id so pagination is stable across page boundaries —
                    // without this, items added/removed mid-sync can dupe or skip.
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting items from library {LibraryId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets top-level items from a library (non-recursive) for browsing/filtering UI.
    /// Returns series, movies, or top-level folders depending on library type.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="searchTerm">Optional search term to filter results.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with top-level items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetTopLevelLibraryItemsAsync(
        Guid libraryId,
        string? searchTerm = null,
        int startIndex = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.Overview, ItemFields.DateCreated };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.EnableImageTypes = new[] { ImageType.Primary };
                    config.QueryParameters.ImageTypeLimit = 1;

                    if (!string.IsNullOrWhiteSpace(searchTerm))
                    {
                        // When searching, use recursive so Jellyfin's search engine works,
                        // but restrict to top-level container types only (no episodes/seasons/tracks)
                        config.QueryParameters.SearchTerm = searchTerm;
                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet,
                            BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist
                        };
                    }
                    else
                    {
                        // Non-recursive browse — include standalone files too (Audio, Video)
                        // since they appear at the top level in some library types
                        config.QueryParameters.Recursive = false;
                        config.QueryParameters.IncludeItemTypes = new[]
                        {
                            BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet,
                            BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist,
                            BaseItemKind.Audio, BaseItemKind.Video
                        };
                    }
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top-level items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets items from a library with extended metadata fields for metadata sync.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items including metadata.</returns>
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemsWithMetadataAsync(
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        ItemFields[]? fields = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    // Caller decides which fields to request based on what
                    // metadata categories are actually enabled. Sending
                    // ItemFields.People for a library where the user has
                    // MetadataSyncPeople=false bloats every page response.
                    config.QueryParameters.Fields = fields ?? new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.Overview,
                        ItemFields.Genres,
                        ItemFields.Tags,
                        ItemFields.Studios,
                        ItemFields.People,
                        ItemFields.ProviderIds,
                        ItemFields.OriginalTitle,
                        ItemFields.SortName,
                        ItemFields.ProductionLocations,
                        ItemFields.Taglines,
                        ItemFields.Settings,     // For LockedFields, PreferredMetadataLanguage, PreferredMetadataCountryCode
                        ItemFields.CustomRating  // For CustomRating field
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items with metadata from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets the total count of items in a library without fetching item details.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total item count or 0 if failed.</returns>
    public async Task<int> GetLibraryItemCountAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting item count for library {LibraryId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// Gets folder-type items (Series, Season, MusicAlbum, MusicArtist, BoxSet) with full metadata fields.
    /// Used for syncing metadata on container/parent items.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Pagination start index.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with folder items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetLibraryFolderItemsWithMetadataAsync(
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        ItemFields[]? fields = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[]
                    {
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.MusicAlbum,
                        BaseItemKind.MusicArtist,
                        BaseItemKind.BoxSet
                    };
                    config.QueryParameters.Fields = fields ?? new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.Overview,
                        ItemFields.Genres,
                        ItemFields.Tags,
                        ItemFields.Studios,
                        ItemFields.People,
                        ItemFields.ProviderIds,
                        ItemFields.OriginalTitle,
                        ItemFields.SortName,
                        ItemFields.ProductionLocations,
                        ItemFields.Taglines,
                        ItemFields.Settings,
                        ItemFields.CustomRating
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get folder items with metadata from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets the total count of folder-type items in a library.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total folder item count or 0 if failed.</returns>
    public async Task<int> GetLibraryFolderItemCountAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[]
                    {
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.MusicAlbum,
                        BaseItemKind.MusicArtist,
                        BaseItemKind.BoxSet
                    };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get folder item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// Lightweight discovery fetch — returns only Id and Path for items in a
    /// library. Used by Metadata refresh to determine which source items have
    /// a local correlate before paying for the full metadata payload. The
    /// per-page response is dramatically smaller than the full
    /// <c>GetLibraryItemsWithMetadataAsync</c> response (no Genres/Studios/
    /// Tags/People/Overview/etc.), so the discovery scan is cheap even on
    /// huge libraries.
    /// </summary>
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemPathsAsync(
        Guid libraryId,
        BaseItemKind[] includeTypes,
        int startIndex = 0,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = includeTypes;
                    config.QueryParameters.Fields = new[] { ItemFields.Path };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} getting item paths from library {LibraryId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item paths from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// Batch-fetches full metadata for a specific set of item IDs. Used by
    /// Metadata refresh after the discovery pass: only items that exist
    /// locally are fetched with their heavy <see cref="ItemFields"/>, so we
    /// don't pay for source items the user doesn't have.
    /// </summary>
    public async Task<List<BaseItemDto>> GetItemsByIdsAsync(
        IReadOnlyList<Guid> ids,
        ItemFields[] fields,
        int batchSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = new List<BaseItemDto>();
        if (ids.Count == 0)
        {
            return result;
        }

        for (var i = 0; i < ids.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();

            try
            {
                var client = GetApiClient();
                var response = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Ids = chunk;
                        config.QueryParameters.Fields = fields;
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    result.AddRange(response.Items);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
            {
                // Any 4xx/5xx means the source didn't give us a real answer —
                // not that the requested items don't exist. Letting this
                // swallow returns a partial result and downstream pruning
                // then deletes every local row that "wasn't seen this run."
                // Bubble up so the refresh aborts before the prune step.
                _logger.LogWarning(ex, "Source server {Status} fetching items batch at index {Index}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, i);
                throw;
            }
            catch (Exception ex)
            {
                // Transport faults (HttpRequestException, deserialization) are
                // "no real answer" just like a 5xx — a swallowed chunk here
                // gets its rows pruned and loses the user's Ignored overrides.
                _logger.LogWarning(ex, "Failed to fetch items batch starting at index {Index}; aborting refresh to protect tracking rows", i);
                throw;
            }
        }

        return result;
    }

    /// <summary>
    /// Gets detailed item info including media sources and streams.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Item details or null if not found.</returns>
    public async Task<BaseItemDto?> GetItemDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.Ids = new Guid?[] { itemId };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources,
                        ItemFields.MediaStreams
                    };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item details for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Downloads a file from the source server.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content and the response Content-Length when the server provided one, or null on failure.</returns>
    public async Task<(Stream Stream, long? ContentLength)?> DownloadFileAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        try
        {
            // /Items/{id}/Download — direct-download URL. Authentication is
            // via the Authorization header only; do NOT mix in api_key /
            // ApiKey query parameters or X-Emby-Token headers. Per the
            // Jellyfin auth spec (10.11+), sending multiple tokens in one
            // request has undefined precedence and may be rejected outright
            // when EnableLegacyAuthorization=false on the source.
            var url = $"{_serverUrl}/Items/{itemId}/Download";
            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            return (new ResponseDisposingStream(stream, response, request), contentLength);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            request?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            request?.Dispose();
            _logger.LogError(ex, "Failed to download item {ItemId}", itemId);
            // Re-throw with a context-rich message so the per-row Reason
            // field surfaces what actually went wrong (timeout, 5xx,
            // connection refused, SSL handshake, etc.) instead of the
            // generic "No response from server". Original exception is
            // preserved as InnerException for stack-trace logging.
            throw new InvalidOperationException(
                $"Source download failed for item {itemId}: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Gets external subtitle companion files for an item. Restricted to
    /// subtitle streams: they are the only external stream type Jellyfin
    /// exposes a download route for
    /// (<c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>).
    /// External audio tracks are skipped with a log — the previous
    /// path-parameter approach silently served the item's main video file in
    /// their place, corrupting the companion on disk.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of companion file info.</returns>
    public async Task<List<CompanionFileInfo>> GetCompanionFilesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var companions = new List<CompanionFileInfo>();

        try
        {
            var item = await GetItemDetailsAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (item?.MediaSources == null || item.MediaSources.Count == 0)
            {
                return companions;
            }

            var mediaSource = item.MediaSources.FirstOrDefault();
            if (mediaSource?.MediaStreams == null)
            {
                return companions;
            }

            var skippedNonSubtitle = 0;
            foreach (var stream in mediaSource.MediaStreams)
            {
                if (stream.IsExternal != true || string.IsNullOrEmpty(stream.Path))
                {
                    continue;
                }

                if (stream.Type != MediaStream_Type.Subtitle)
                {
                    skippedNonSubtitle++;
                    continue;
                }

                companions.Add(new CompanionFileInfo
                {
                    SourcePath = stream.Path!,
                    FileName = Path.GetFileName(stream.Path)!,
                    Language = stream.Language,
                    Codec = stream.Codec,
                    IsExternal = true,
                    StreamIndex = stream.Index ?? 0,
                    MediaSourceId = mediaSource.Id ?? itemId.ToString("N")
                });
            }

            if (skippedNonSubtitle > 0)
            {
                _logger.LogInformation(
                    "Item {ItemId} has {Count} external non-subtitle stream(s) (e.g. audio) that cannot be downloaded via the API; skipping",
                    itemId, skippedNonSubtitle);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get companion files for {ItemId}", itemId);
        }

        return companions;
    }

    /// <summary>
    /// Downloads an external subtitle via the canonical
    /// <c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>
    /// route. The previously used
    /// <c>/Videos/{id}/Subtitles/Stream?path=…</c> shape returns 405 on
    /// Jellyfin 10.11, and its <c>/Items/{id}/File?path=…</c> fallback
    /// ignores the path parameter and serves the item's main media file —
    /// verified against a live 10.11 server — which wrote video bytes into
    /// <c>.srt</c> companions.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="companion">Companion stream to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content or null on failure.</returns>
    public async Task<Stream?> DownloadCompanionFileAsync(Guid itemId, CompanionFileInfo companion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companion);
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        try
        {
            // Stream.{format}: use the source file's own extension so the
            // server returns the subtitle in its original format instead of
            // converting. Codec name is the fallback for extension-less paths
            // (subrip files carry the .srt extension, so map that one).
            var format = Path.GetExtension(companion.SourcePath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
            {
                format = string.Equals(companion.Codec, "subrip", StringComparison.OrdinalIgnoreCase)
                    ? "srt"
                    : companion.Codec?.ToLowerInvariant() ?? "srt";
            }

            var url = $"{_serverUrl}/Videos/{itemId}/{Uri.EscapeDataString(companion.MediaSourceId)}/Subtitles/{companion.StreamIndex}/Stream.{Uri.EscapeDataString(format)}";
            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            return new ResponseDisposingStream(stream, response, request);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            request?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            request?.Dispose();
            _logger.LogError(ex, "Failed to download companion subtitle {FilePath} (stream {Index}) for {ItemId}", companion.SourcePath, companion.StreamIndex, itemId);
            return null;
        }
    }

    // ===== User Sync Methods =====

    /// <summary>
    /// Gets a user's full details including Policy and Configuration.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User details or null if not found.</returns>
    public async Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Users[userId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the User Refresh treats it as source-
            // unavailable and skips pruning, rather than deleting rows for a
            // user it merely couldn't fetch.
            _logger.LogWarning(ex, "Source server {Status} getting user details for {UserId}; refresh will skip pruning to protect tracking rows", ex.ResponseStatusCode, userId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user details for {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's profile image as a stream.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image stream or null if not found.</returns>
    public async Task<Stream?> GetUserImageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        try
        {
            var url = $"{_serverUrl}/Users/{userId}/Images/Primary";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // User has no profile image
                    response.Dispose();
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseDisposingStream(stream, response);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogError(ex, "Failed to get profile image for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets both the SHA256 hash and content length of a user's profile image in a single download.
    /// This avoids the overhead of separate GET (for hash) and HEAD (for size) requests.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (hash, size), or (null, null) if no image or on error.</returns>
    public async Task<(string? Hash, long? Size)> GetUserImageHashAndSizeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var imageStream = await GetUserImageAsync(userId, cancellationToken).ConfigureAwait(false);
            if (imageStream == null)
            {
                return (null, null);
            }

            // Read the stream to compute hash and count bytes simultaneously
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var buffer = new byte[8192];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await imageStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                totalBytes += bytesRead;
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = Convert.ToHexString(sha256.Hash!).ToLowerInvariant()[..32];

            return (hash, totalBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get profile image hash and size for user {UserId}", userId);
            return (null, null);
        }
    }

    /// <summary>
    /// Gets image info for an item, including sizes and dimensions.
    /// </summary>
    /// <param name="itemId">Item ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of image info or null if failed.</returns>
    public async Task<List<ImageInfo>?> GetItemImageInfoAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items[itemId].Images.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get image info for item {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// HEADs the image endpoint and returns the byte size from Content-Length,
    /// or null if the request fails or the server omits the header. Bypasses
    /// the source server's cached <see cref="ImageInfo.Size"/> (which is
    /// populated from Jellyfin's in-memory item state and goes stale when an
    /// image file is replaced on disk without re-scanning), so the
    /// post-replacement size shows up in the table on the next refresh.
    /// </summary>
    public async Task<long?> GetItemImageContentLengthAsync(
        Guid itemId,
        string imageType,
        int? imageIndex,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head,
            imageIndex.HasValue
                ? $"{_serverUrl}/Items/{itemId}/Images/{imageType}/{imageIndex.Value}"
                : $"{_serverUrl}/Items/{itemId}/Images/{imageType}");
        AddAuthorizationHeader(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HEAD for image size failed: {ImageType}/{Index} on {ItemId}", imageType, imageIndex, itemId);
            return null;
        }
    }

    /// <summary>
    /// Downloads an item image from the source server as a stream.
    /// </summary>
    /// <param name="itemId">Item ID on the source server.</param>
    /// <param name="imageType">Image type name (e.g., "Primary", "Backdrop").</param>
    /// <param name="imageIndex">Image index (for multi-image types like Backdrop). Null for single images.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (stream, contentType) or (null, null) on failure.</returns>
    public async Task<(Stream? Stream, string? ContentType)> DownloadItemImageAsync(
        Guid itemId,
        string imageType,
        int? imageIndex,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
        try
        {
            var url = imageIndex.HasValue
                ? $"{_serverUrl}/Items/{itemId}/Images/{imageType}/{imageIndex.Value}"
                : $"{_serverUrl}/Items/{itemId}/Images/{imageType}";

            request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download image {ImageType}/{Index} for item {ItemId}: {StatusCode}",
                    imageType, imageIndex, itemId, response.StatusCode);
                response.Dispose();
                request.Dispose();
                return (null, null);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            return (new ResponseDisposingStream(stream, response, request), contentType);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            request?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            request?.Dispose();
            // Bumped from LogDebug — failures here were invisible at the
            // default Jellyfin log level and made "verify says local empty,
            // source non-empty" failures impossible to root-cause without
            // turning on plugin debug logs.
            _logger.LogWarning(ex, "Failed to download image {ImageType}/{Index} for item {ItemId}", imageType, imageIndex, itemId);
            return (null, null);
        }
    }

    // ===== People Sync Methods =====

    /// <summary>
    /// Bulk-fetches every Person from the source server via paginated
    /// <c>/Items?includeItemTypes=Person&amp;recursive=true</c> calls so
    /// callers can do a local name-keyed join instead of one HTTP call per
    /// person. The <c>/Persons</c> endpoint silently ignores
    /// <c>StartIndex</c> (verified against a live 10.11 server: the same
    /// first page repeats), so the generic Items endpoint is the only way to
    /// page the catalog. Also verified live: for every person <c>/Persons</c>
    /// returns, <c>/Items</c> returns a byte-identical DTO with the same
    /// field set, plus a handful of extra Person entries (music artists) that
    /// are harmless — the People refresh intersects by name against local
    /// persons. Throws on any failure: a partial catalog would make the
    /// People refresh prune rows for persons it never saw.
    /// </summary>
    /// <returns>All persons returned by the source server.</returns>
    public async Task<IReadOnlyList<BaseItemDto>> GetAllPersonsAsync(
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 1000;
        var persons = new List<BaseItemDto>();
        var startIndex = 0;

        try
        {
            var client = GetApiClient();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Person };
                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.Fields = new[]
                        {
                            ItemFields.Overview,
                            ItemFields.ProviderIds,
                            ItemFields.Tags,
                            ItemFields.OriginalTitle,
                            ItemFields.SortName,
                            ItemFields.DateCreated,
                            ItemFields.ProductionLocations,
                            // Settings populates LockData + LockedFields;
                            // without it those come back null while local has
                            // them populated, producing a perpetual false diff.
                            ItemFields.Settings
                        };
                        config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                        config.QueryParameters.StartIndex = startIndex;
                        config.QueryParameters.Limit = pageSize;
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (page?.Items == null)
                {
                    // A null page mid-catalog is a failure, not end-of-list —
                    // treating it as done would return a partial catalog.
                    throw new InvalidOperationException(
                        $"Source server returned no response fetching Persons page at index {startIndex}");
                }

                persons.AddRange(page.Items);

                if (page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value)
                {
                    break;
                }

                if (page.Items.Count < pageSize)
                {
                    break;
                }

                startIndex += page.Items.Count;
            }

            return persons;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx isn't "source has no persons" — it's "source didn't
            // give us a real answer." Returning empty here makes the People
            // refresh prune every local tracking row. Bubble up so the refresh
            // aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} bulk-fetching Persons; aborting refresh to protect tracking rows", ex.ResponseStatusCode);
            throw;
        }
        catch (Exception ex)
        {
            // Same reasoning as the 4xx/5xx case: a transport fault, timeout, or
            // deserialization error is "no real answer," not "source has no
            // persons." The People refresh has no incomplete-discovery prune
            // guard, so returning empty here would prune every tracking row.
            // Bubble up so the refresh aborts before prune.
            _logger.LogWarning(ex, "Failed to bulk-fetch Persons from source server; aborting refresh to protect tracking rows");
            throw;
        }
    }


    // ===== History Sync Methods =====

    /// <summary>
    /// Batch-fetches items by ID with the specified user's UserData populated.
    /// Used by the History refresh task after the local-first discovery pass —
    /// only items whose translated path exists locally get user-data fetched,
    /// rather than the prior approach of pulling every user-played item per
    /// (user × library) and discarding most on path mismatch.
    /// </summary>
    public async Task<BaseItemDtoQueryResult?> GetItemsWithUserDataByIdsAsync(
        Guid userId,
        Guid?[] ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Length == 0)
        {
            return null;
        }

        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.Ids = ids;
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the History Refresh aborts before prune.
            _logger.LogWarning(ex, "Source server {Status} fetching user-data items for user {UserId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user-data items for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets items with user playback data for a specific user in a library.
    /// Uses the Items endpoint with UserId parameter to get user-specific data.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items including user data.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserLibraryItemsAsync(
        Guid userId,
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Fetches every item ID a user has played within a library, paginated.
    /// Throws on transient failure so callers don't conflate "error" with
    /// "user played nothing" — a silent empty return would let downstream
    /// filters skip items they shouldn't.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source server returns a null page (failure) rather
    /// than an explicit empty page.
    /// </exception>
    public async Task<HashSet<Guid>> GetUserPlayedItemIdsAsync(
        Guid userId,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 200;
        var ids = new HashSet<Guid>();
        var startIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await GetUserPlayedItemsAsync(userId, libraryId, startIndex, pageSize, cancellationToken).ConfigureAwait(false);
            if (page == null)
            {
                // GetUserPlayedItemsAsync returns null on a logged transient
                // failure. Treating that as end-of-list would silently classify
                // every item as "user did not play this" — wrong answer.
                throw new InvalidOperationException(
                    $"Failed to fetch played items page for user {userId} in library {libraryId} at index {startIndex}");
            }

            if (page.Items == null || page.Items.Count == 0)
            {
                break;
            }

            foreach (var item in page.Items)
            {
                if (item.Id.HasValue)
                {
                    ids.Add(item.Id.Value);
                }
            }

            // Bound the loop with TotalRecordCount when the server provides it,
            // so a stable-sort hiccup that returns a full page repeatedly cannot
            // run startIndex past the actual total.
            if (page.TotalRecordCount.HasValue && startIndex + page.Items.Count >= page.TotalRecordCount.Value)
            {
                break;
            }

            if (page.Items.Count < pageSize)
            {
                break;
            }

            startIndex += pageSize;
        }

        return ids;
    }

    /// <summary>
    /// Gets items that have been played by a specific user in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with played items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserPlayedItemsAsync(
        Guid userId,
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.IsPlayed = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode >= 400)
        {
            // Any 4xx/5xx: bubble up so the History Refresh aborts before prune.
            // GetUserPlayedItemIdsAsync already throws on null page, so a rethrow
            // here propagates the same way.
            _logger.LogWarning(ex, "Source server {Status} getting played items for user {UserId} in library {LibraryId}; aborting refresh to protect tracking rows", ex.ResponseStatusCode, userId, libraryId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get played items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets count of items with history data for a user in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total count of items with history.</returns>
    public async Task<int> GetUserLibraryItemCountAsync(
        Guid userId,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for user {UserId} in library {LibraryId}", userId, libraryId);
            return 0;
        }
    }

    /// <summary>
    /// <summary>
    /// Adds the canonical Jellyfin <c>Authorization: MediaBrowser ...</c>
    /// header to a request. Format per Jellyfin's auth spec:
    /// <c>MediaBrowser key="value", key2="value2"</c>. Per Jellyfin docs,
    /// the Authorization header is the only non-deprecated method for
    /// transmitting credentials; <c>X-Emby-Token</c>, <c>X-MediaBrowser-Token</c>,
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
        var authValue = $"Client=\"{DefaultClientName}\", Device=\"{_localServerName}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{_pluginVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
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

                    // Do NOT dispose _httpClient — it is externally owned (from IHttpClientFactory).
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
            // keep an inner HttpConnection read blocked past cancellation —
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
                // Already disposed or in shutdown — nothing useful to do here.
            }
        }
    }
}
