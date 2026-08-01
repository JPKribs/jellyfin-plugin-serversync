using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Configuration and connection endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Tests connection to the source server using API key authentication.
    /// </summary>
    /// <param name="request">Connection test request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test response.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
    {
        var urlValidation = ValidateServerUrl(request.ServerUrl);
        if (!urlValidation.IsValid)
        {
            return Ok(new ConnectionTestResult
            {
                Success = false,
                Message = urlValidation.Message
            });
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return Ok(new ConnectionTestResult
            {
                Success = false,
                Message = "API key is required"
            });
        }

        // The factory re-runs the SSRF gate and throws on rejection. Without this
        // catch the settings page got an opaque 500 instead of the reason.
        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(urlValidation.NormalizedUrl!, ResolveRequestApiKey(request.ApiKey));
        }
        catch (ArgumentException ex)
        {
            return Ok(new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Message = ex.Message
            });
        }

        using (client)
        {
            return Ok(await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Validates a server URL format and accessibility.
    /// </summary>
    /// <param name="request">URL validation request.</param>
    /// <returns>URL validation response.</returns>
    [HttpPost("ValidateUrl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ValidateUrlResponse> ValidateUrl([FromBody] ValidateUrlRequest request)
    {
        return Ok(ValidateServerUrl(request.Url));
    }

    /// <summary>
    /// Authenticates with a source server using username and password to generate an access token.
    /// </summary>
    /// <param name="request">Authentication request with credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication response with access token if successful.</returns>
    [HttpPost("Authenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthenticateResponse>> Authenticate([FromBody] AuthenticateRequest request, CancellationToken cancellationToken)
    {
        var urlValidation = ValidateServerUrl(request.ServerUrl);
        if (!urlValidation.IsValid)
        {
            return Ok(new AuthenticateResponse
            {
                Success = false,
                Message = urlValidation.Message
            });
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Ok(new AuthenticateResponse
            {
                Success = false,
                Message = "Username is required"
            });
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return Ok(new AuthenticateResponse
            {
                Success = false,
                Message = "Password is required"
            });
        }

        var result = await SourceServerClient.AuthenticateAsync(
            _httpClientFactory,
            urlValidation.NormalizedUrl!,
            request.Username,
            request.Password,
            _configManager.LocalServerName,
            _configManager.PluginVersion,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            result.Message ??= "Authentication failed";
            return Ok(result);
        }

        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(urlValidation.NormalizedUrl!, result.AccessToken!);
        }
        catch (ArgumentException ex)
        {
            result.Success = false;
            result.Message = ex.Message;
            return Ok(result);
        }

        using (client)
        {
            var connectionTest = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);

            result.ServerName = connectionTest.ServerName;
            result.ServerId = connectionTest.ServerId;
            result.Message = "Authentication successful";
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets libraries from the source server.
    /// </summary>
    /// <param name="request">Connection request with credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of library DTOs.</returns>
    [HttpPost("GetSourceLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<LibraryDto>>> GetSourceLibraries(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var urlValidation = ValidateServerUrl(request.ServerUrl);
        if (!urlValidation.IsValid)
        {
            return BadRequest(urlValidation.Message);
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("API key is required");
        }

        try
        {
            using var client = _clientFactory.Create(urlValidation.NormalizedUrl!, ResolveRequestApiKey(request.ApiKey));

            // Pass authenticated user ID for non-admin fallback
            var config = Plugin.Instance?.Configuration;
            var authenticatedUserId = config?.SourceServerAuthenticatedUserId;

            var libraries = await client.GetLibrariesAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);

            return Ok(libraries.Select(l => new LibraryDto
            {
                Id = l.ItemId ?? string.Empty,
                Name = l.Name ?? string.Empty,
                Locations = l.Locations?.ToList() ?? new List<string>()
            }).ToList());
        }
        catch (OperationCanceledException)
        {
            throw; // Let ASP.NET Core handle cancellation
        }
        catch (ArgumentException ex)
        {
            // SSRF gate in the factory rejected the URL; surface the reason.
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to get libraries from source server");
            return BadRequest("Failed to connect to source server. Check server logs for details.");
        }
    }

    /// <summary>
    /// Gets users from the source server.
    /// </summary>
    /// <param name="request">Connection request with credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user info DTOs.</returns>
    [HttpPost("GetSourceUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<UserInfoDto>>> GetSourceUsers(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var urlValidation = ValidateServerUrl(request.ServerUrl);
        if (!urlValidation.IsValid)
        {
            return BadRequest(urlValidation.Message);
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("API key is required");
        }

        try
        {
            using var client = _clientFactory.Create(urlValidation.NormalizedUrl!, ResolveRequestApiKey(request.ApiKey));

            // Pass authenticated user ID for non-admin fallback
            var config = Plugin.Instance?.Configuration;
            var authenticatedUserId = config?.SourceServerAuthenticatedUserId;

            var users = await client.GetUsersAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);

            return Ok(users.Select(u => new UserInfoDto
            {
                Id = u.Id?.ToString() ?? string.Empty,
                Name = u.Name ?? string.Empty
            }).ToList());
        }
        catch (OperationCanceledException)
        {
            throw; // Let ASP.NET Core handle cancellation
        }
        catch (ArgumentException ex)
        {
            // SSRF gate in the factory rejected the URL; surface the reason.
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to get users from source server");
            return BadRequest("Failed to connect to source server. Check server logs for details.");
        }
    }

    /// <summary>
    /// Gets top-level items from a source server library for browsing/filtering.
    /// </summary>
    /// <param name="libraryId">Source library ID.</param>
    /// <param name="search">Optional search term.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of source files.</returns>
    [HttpGet("SourceLibraryItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SourceLibraryItemsResponse>> GetSourceLibraryItems(
        [FromQuery] string libraryId,
        [FromQuery] string? search = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        [FromQuery] bool collections = false,
        [FromQuery] bool playlists = false,
        CancellationToken cancellationToken = default)
    {
        if (!collections && !playlists && string.IsNullOrWhiteSpace(libraryId))
        {
            return BadRequest("Library ID is required");
        }

        // Same clamps as every other list endpoint — an unclamped take pulls
        // an entire source library into one response.
        startIndex = Math.Max(0, startIndex);
        limit = Math.Clamp(limit, 1, 200);
        if (skip.HasValue)
        {
            skip = Math.Max(0, skip.Value);
        }

        if (take.HasValue)
        {
            take = Math.Clamp(take.Value, 1, 200);
        }

        var config = _configManager.Configuration;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            return BadRequest("Source server is not configured");
        }

        if (!collections && !playlists && !Guid.TryParse(libraryId, out _))
        {
            return BadRequest("Invalid library ID format");
        }

        try
        {
            using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

            // Collections and playlists live outside libraries (server-wide
            // meta-folders), so the picker browses them without a parent
            // library scope.
            var result = collections
                ? await client.GetCollectionsAsync(
                    search,
                    skip ?? startIndex,
                    take ?? limit,
                    cancellationToken).ConfigureAwait(false)
                : playlists
                    ? await client.GetPlaylistsAsync(
                        search,
                        skip ?? startIndex,
                        take ?? limit,
                        cancellationToken).ConfigureAwait(false)
                    : await client.GetTopLevelLibraryItemsAsync(
                        Guid.Parse(libraryId),
                        search,
                        skip ?? startIndex,
                        take ?? limit,
                        cancellationToken).ConfigureAwait(false);

            if (result?.Items == null)
            {
                return Ok(new SourceLibraryItemsResponse { Items = new List<SourceLibraryItemDto>(), TotalCount = 0 });
            }

            var items = result.Items.Select(item => new SourceLibraryItemDto
            {
                Id = item.Id?.ToString("N") ?? string.Empty,
                Name = item.Name ?? string.Empty,
                Year = item.ProductionYear,
                Overview = item.Overview,
                Path = item.Path ?? string.Empty,
                Type = item.Type?.ToString()
            }).ToList();

            return Ok(new SourceLibraryItemsResponse
            {
                Items = items,
                TotalCount = result.TotalRecordCount ?? items.Count
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get source files for library {LibraryId}", libraryId);
            return BadRequest("Failed to fetch items from source server");
        }
    }

    /// <summary>
    /// Validates and normalizes a server URL. Classification is delegated to
    /// <see cref="ConfigurationUtilities.ValidateServerUrlForSsrf"/> — the same
    /// gate <see cref="ISourceServerClientFactory.Create"/> enforces. A second,
    /// weaker copy lived here and ignored
    /// <c>AllowSourceServerOnPrivateNetwork</c>, so a rejected URL passed this
    /// check and then threw out of the factory as an unhandled 500 instead of
    /// surfacing the reason the code had already written.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <returns>Validation response with normalized URL.</returns>
    private ValidateUrlResponse ValidateServerUrl(string url)
    {
        var ssrfError = ConfigurationUtilities.ValidateServerUrlForSsrf(
            url,
            _configManager.Configuration.AllowSourceServerOnPrivateNetwork);
        if (ssrfError != null)
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = ssrfError
            };
        }

        // Guaranteed to parse — ValidateServerUrlForSsrf rejects anything that doesn't.
        var uri = new Uri(url, UriKind.Absolute);

        var isLocalhost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                          uri.Host.Equals("::1", StringComparison.Ordinal);

        var normalizedUrl = $"{uri.Scheme}://{uri.Host}";
        if (!uri.IsDefaultPort)
        {
            normalizedUrl += $":{uri.Port}";
        }

        // Keep a sub-path (reverse proxy serving Jellyfin at /jellyfin) —
        // dropping it made such servers impossible to configure, with a
        // misleading "connection failed" as the only symptom.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            normalizedUrl += path;
        }

        return new ValidateUrlResponse
        {
            IsValid = true,
            NormalizedUrl = normalizedUrl,
            Message = isLocalhost ? "Warning: Using localhost URL. Make sure this is intentional." : null
        };
    }
}
