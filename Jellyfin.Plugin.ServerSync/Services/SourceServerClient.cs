using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SourceServerClient
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
    /// TestConnectionAsync
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Authenticated /System/Info failed, falling back to /System/Info/Public");
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

        // Add the required MediaBrowser authorization header (without token for auth request)
        var authHeader = $"MediaBrowser Client=\"{DefaultClientName}\", Device=\"{localServerName}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{pluginVersion}\"";
        content.Headers.Add("X-Emby-Authorization", authHeader);

        try
        {
            using var response = await httpClient.PostAsync(
                $"{serverUrl}/Users/AuthenticateByName",
                content,
                cancellationToken).ConfigureAwait(false);

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
    /// GetLibrariesAsync
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
    /// GetUsersAsync
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
    /// GetLibraryItemsAsync
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
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources, ItemFields.Etag };
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top-level items from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// GetLibraryItemsWithMetadataAsync
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
                    config.QueryParameters.Fields = new[]
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
                        ItemFields.CustomRating, // For CustomRating field
                        ItemFields.Etag          // For change detection in SkipUnchanged refresh mode
                    };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items with metadata from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// GetLibraryItemCountAsync
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
                    config.QueryParameters.Fields = new[]
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
                        ItemFields.CustomRating,
                        ItemFields.Etag
                    };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get folder item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// GetItemDetailsAsync
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item details for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// DownloadFileAsync
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
            return null;
        }
    }

    /// <summary>
    /// GetCompanionFilesAsync
    /// Gets external companion files (subtitles, etc.) for an item.
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

            companions.AddRange(
                mediaSource.MediaStreams
                    .Where(stream => stream.IsExternal == true && !string.IsNullOrEmpty(stream.Path))
                    .Select(stream => new CompanionFileInfo
                    {
                        SourcePath = stream.Path!,
                        FileName = Path.GetFileName(stream.Path)!,
                        Language = stream.Language,
                        Codec = stream.Codec,
                        IsExternal = true,
                        StreamIndex = stream.Index ?? 0
                    }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get companion files for {ItemId}", itemId);
        }

        return companions;
    }

    /// <summary>
    /// DownloadCompanionFileAsync
    /// Downloads an external subtitle or companion file by its path.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="filePath">Source file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content or null on failure.</returns>
    public async Task<Stream?> DownloadCompanionFileAsync(Guid itemId, string filePath, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        HttpRequestMessage? activeRequest = null;
        try
        {
            var encodedPath = Uri.EscapeDataString(filePath);
            var url = $"{_serverUrl}/Videos/{itemId}/Subtitles/Stream?mediaSourceId={itemId}&path={encodedPath}";

            activeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(activeRequest);

            response = await _httpClient.SendAsync(activeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Dispose the failed response and request before making the fallback request
                response.Dispose();
                activeRequest.Dispose();

                var directUrl = $"{_serverUrl}/Items/{itemId}/File?path={encodedPath}";
                activeRequest = new HttpRequestMessage(HttpMethod.Get, directUrl);
                AddAuthorizationHeader(activeRequest);

                response = await _httpClient.SendAsync(activeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Transfer ownership of request and response to the stream wrapper
            return new ResponseDisposingStream(stream, response, activeRequest);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            activeRequest?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            activeRequest?.Dispose();
            _logger.LogError(ex, "Failed to download companion file {FilePath} for {ItemId}", filePath, itemId);
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get image info for item {ItemId}", itemId);
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
            _logger.LogDebug(ex, "Failed to download image {ImageType}/{Index} for item {ItemId}", imageType, imageIndex, itemId);
            return (null, null);
        }
    }

    // ===== People Sync Methods =====

    /// <summary>
    /// Gets a single Person by name from the source server as a full BaseItemDto.
    /// First resolves the person ID via the Persons endpoint, then fetches the full
    /// item with all metadata fields via the Items endpoint.
    /// </summary>
    /// <param name="name">Person name to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>BaseItemDto for the person, or null on failure.</returns>
    public async Task<BaseItemDto?> GetPersonByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();

            var personStub = await client.Persons[name].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (personStub?.Id == null)
            {
                return null;
            }

            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.Ids = new Guid?[] { personStub.Id };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview,
                        ItemFields.ProviderIds,
                        ItemFields.Tags,
                        ItemFields.OriginalTitle,
                        ItemFields.SortName,
                        ItemFields.Settings,
                        ItemFields.DateCreated
                    };
                },
                cancellationToken).ConfigureAwait(false);

            return result?.Items?.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Person not found or failed to fetch from source server: {PersonName}", name);
            return null;
        }
    }

    /// <summary>
    /// Gets multiple persons by name from the source server as full BaseItemDtos.
    /// Uses the /Items endpoint with IncludeItemTypes=Person and paginated StartIndex/Limit
    /// to fetch all persons with full metadata, then filters to the requested names.
    /// </summary>
    /// <param name="names">Person names to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="pageSize">Number of persons to fetch per page.</param>
    /// <returns>Dictionary of person name to BaseItemDto.</returns>
    public async Task<Dictionary<string, BaseItemDto>> GetPersonsByNamesAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken,
        int pageSize = 500)
    {
        var result = new Dictionary<string, BaseItemDto>(StringComparer.OrdinalIgnoreCase);

        if (names.Count == 0)
        {
            return result;
        }

        var neededNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var client = GetApiClient();
        int startIndex = 0;
        int totalFetched = 0;

        // Paginate through all Person items on the source server
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var page = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Person };
                        config.QueryParameters.StartIndex = startIndex;
                        config.QueryParameters.Limit = pageSize;
                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.Fields = new[]
                        {
                            ItemFields.Overview,
                            ItemFields.ProviderIds,
                            ItemFields.Tags,
                            ItemFields.OriginalTitle,
                            ItemFields.SortName,
                            ItemFields.Settings,
                            ItemFields.DateCreated
                        };
                    },
                    cancellationToken).ConfigureAwait(false);

                if (page?.Items == null || page.Items.Count == 0)
                {
                    break;
                }

                foreach (var person in page.Items)
                {
                    if (!string.IsNullOrEmpty(person.Name) && neededNames.Contains(person.Name))
                    {
                        result[person.Name] = person;
                    }
                }

                totalFetched += page.Items.Count;
                startIndex += page.Items.Count;

                _logger.LogDebug(
                    "Fetched person page: {PageCount} items (total {Total}), matched {Matched}/{Needed} so far",
                    page.Items.Count, totalFetched, result.Count, neededNames.Count);

                // Stop if we've received fewer items than requested (last page)
                // or we've found all the names we need
                if (page.Items.Count < pageSize || result.Count >= neededNames.Count)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch persons page at offset {StartIndex}", startIndex);
                break;
            }
        }

        _logger.LogInformation(
            "Fetched {Total} persons from source in {Pages} page(s), matched {Matched}/{Needed} requested names",
            totalFetched,
            (totalFetched + pageSize - 1) / pageSize,
            result.Count,
            neededNames.Count);

        // Fallback: individual lookups for names not found in the paginated fetch
        if (result.Count < neededNames.Count)
        {
            var missingNames = neededNames.Where(n => !result.ContainsKey(n)).ToList();
            _logger.LogDebug("Falling back to individual lookups for {Count} unresolved person(s)", missingNames.Count);

            await Parallel.ForEachAsync(
                missingNames,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken
                },
                async (name, ct) =>
                {
                    var person = await GetPersonByNameAsync(name, ct).ConfigureAwait(false);
                    if (person != null && !string.IsNullOrEmpty(person.Name))
                    {
                        lock (result)
                        {
                            result[name] = person;
                        }
                    }
                }).ConfigureAwait(false);

            _logger.LogInformation("After fallback: matched {Matched}/{Needed} requested names", result.Count, neededNames.Count);
        }

        return result;
    }

    /// <summary>
    /// Re-fetches persons by their IDs via /Items?Ids=... in batches.
    /// Used to get complete metadata for persons where the bulk IncludeItemTypes=Person
    /// fetch returned incomplete data (e.g., missing Overview).
    /// </summary>
    /// <param name="ids">Person IDs to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="batchSize">Number of IDs per request.</param>
    /// <returns>List of fully-populated BaseItemDto results.</returns>
    public async Task<List<BaseItemDto>> GetPersonsByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken,
        int batchSize = 50)
    {
        var result = new List<BaseItemDto>();

        for (int i = 0; i < ids.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = ids.Skip(i).Take(batchSize).Cast<Guid?>().ToArray();

            try
            {
                var client = GetApiClient();
                var itemsResult = await client.Items.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Ids = chunk;
                        config.QueryParameters.Fields = new[]
                        {
                            ItemFields.Overview,
                            ItemFields.ProviderIds,
                            ItemFields.Tags,
                            ItemFields.OriginalTitle,
                            ItemFields.SortName,
                            ItemFields.Settings,
                            ItemFields.DateCreated
                        };
                    },
                    cancellationToken).ConfigureAwait(false);

                if (itemsResult?.Items != null)
                {
                    result.AddRange(itemsResult.Items);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch persons by IDs at batch offset {Index}", i);
            }
        }

        return result;
    }

    // ===== History Sync Methods =====

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
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Fetches the full set of item IDs played by a user within a library, paginating internally.
    /// Returns an empty set on failure so callers can treat it as "no items played."
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Set of item IDs the user has played in the library.</returns>
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
            if (page?.Items == null || page.Items.Count == 0)
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
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get played items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets the user's favorite items in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with favorite items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserFavoritesAsync(
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
                    config.QueryParameters.IsFavorite = true;
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get favorites for user {UserId} in library {LibraryId}", userId, libraryId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for user {UserId} in library {LibraryId}", userId, libraryId);
            return 0;
        }
    }

    /// <summary>
    /// AddAuthorizationHeader
    /// Adds the MediaBrowser authorization header to HTTP requests.
    /// </summary>
    /// <param name="request">HTTP request message.</param>
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        var authValue = $"MediaBrowser Client=\"{DefaultClientName}\", Device=\"{_localServerName}\", DeviceId=\"{DefaultDeviceId}\", Version=\"{_pluginVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

    /// <summary>
    /// GetApiClient
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

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
    }
}
