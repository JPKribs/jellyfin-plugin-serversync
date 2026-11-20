using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The routes that test a connection, sign in, and browse another server while a server entry is set up.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Tests the connection to another server with its API Key.
    /// </summary>
    /// <param name="request">The Server URL and API Key to test.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>What the server reported, or why the test failed.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection([FromBody] TestConnectionRequest request, CancellationToken cancellationToken)
    {
        var urlValidation = await ValidateServerUrlAsync(request.ServerUrl, request.AllowPrivateNetwork, cancellationToken).ConfigureAwait(false);
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
                Message = "Enter the API Key."
            });
        }

        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(new SourceServer
            {
                Url = urlValidation.NormalizedUrl!,
                ApiKey = ResolveRequestApiKey(request.ApiKey, request.ServerKey, urlValidation.NormalizedUrl),
                AllowPrivateNetwork = request.AllowPrivateNetwork
            });
        }
        // The factory runs the SSRF guard again, and the page shows its reason.
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
            var tested = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (tested.Success)
            {
                (tested.SyncAccess, tested.SyncAccessMessage) = await client.CheckSyncAccessAsync(cancellationToken).ConfigureAwait(false);
            }

            return Ok(tested);
        }
    }

    /// <summary>
    /// Signs in to another server with a username and password to get an access token.
    /// </summary>
    /// <param name="request">The Server URL and the credentials to sign in with.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>The access token when the sign in worked, or why it did not.</returns>
    [HttpPost("Authenticate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthenticateResponse>> Authenticate([FromBody] AuthenticateRequest request, CancellationToken cancellationToken)
    {
        var urlValidation = await ValidateServerUrlAsync(request.ServerUrl, request.AllowPrivateNetwork, cancellationToken).ConfigureAwait(false);
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
                Message = "Enter the username."
            });
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return Ok(new AuthenticateResponse
            {
                Success = false,
                Message = "Enter the password."
            });
        }

        var result = await SourceServerClient.AuthenticateAsync(
            _httpClientFactory,
            urlValidation.NormalizedUrl!,
            request.Username,
            request.Password,
            _configManager.LocalServerName,
            _configManager.PluginVersion,
            request.AllowPrivateNetwork,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            result.Message ??= "Authentication failed";
            return Ok(result);
        }

        SourceServerClient client;
        try
        {
            client = _clientFactory.Create(new SourceServer
            {
                Url = urlValidation.NormalizedUrl!,
                ApiKey = result.AccessToken!,
                AllowPrivateNetwork = request.AllowPrivateNetwork
            });
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
            result.Message = "Signed in.";
            if (connectionTest.Success)
            {
                (result.SyncAccess, result.SyncAccessMessage) = await client.CheckSyncAccessAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Lists the libraries of another server.
    /// </summary>
    /// <param name="request">The Server URL and API Key to connect with.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>The libraries, or 400 with the reason.</returns>
    [HttpPost("GetSourceLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<LibraryDto>>> GetSourceLibraries(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var urlValidation = await ValidateServerUrlAsync(request.ServerUrl, request.AllowPrivateNetwork, cancellationToken).ConfigureAwait(false);
        if (!urlValidation.IsValid)
        {
            return BadRequest(urlValidation.Message);
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("Enter the API Key.");
        }

        try
        {
            using var client = _clientFactory.Create(new SourceServer
            {
                Url = urlValidation.NormalizedUrl!,
                ApiKey = ResolveRequestApiKey(request.ApiKey, request.ServerKey, urlValidation.NormalizedUrl),
                AllowPrivateNetwork = request.AllowPrivateNetwork
            });

            var authenticatedUserId = request.AuthenticatedUserId
                ?? _configManager.Configuration.FindServer(request.ServerKey)?.AuthenticatedUserId;

            var libraries = await client.GetLibrariesAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);

            return Ok(libraries.Select(l => new LibraryDto
            {
                Id = l.ItemId ?? string.Empty,
                Name = l.Name ?? string.Empty,
                Locations = l.Locations?.ToList() ?? new List<string>()
            }).ToList());
        }
        // The factory's SSRF guard refused the URL, and the page shows its reason.
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to get libraries from source server");
            return BadRequest("Could not connect to the server. See the server log.");
        }
    }

    /// <summary>
    /// Lists the users of another server.
    /// </summary>
    /// <param name="request">The Server URL and API Key to connect with.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>The users, or 400 with the reason.</returns>
    [HttpPost("GetSourceUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<UserInfoDto>>> GetSourceUsers(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var urlValidation = await ValidateServerUrlAsync(request.ServerUrl, request.AllowPrivateNetwork, cancellationToken).ConfigureAwait(false);
        if (!urlValidation.IsValid)
        {
            return BadRequest(urlValidation.Message);
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest("Enter the API Key.");
        }

        try
        {
            using var client = _clientFactory.Create(new SourceServer
            {
                Url = urlValidation.NormalizedUrl!,
                ApiKey = ResolveRequestApiKey(request.ApiKey, request.ServerKey, urlValidation.NormalizedUrl),
                AllowPrivateNetwork = request.AllowPrivateNetwork
            });

            var authenticatedUserId = request.AuthenticatedUserId
                ?? _configManager.Configuration.FindServer(request.ServerKey)?.AuthenticatedUserId;

            var users = await client.GetUsersAsync(authenticatedUserId, cancellationToken).ConfigureAwait(false);

            return Ok(users.Select(u => new UserInfoDto
            {
                Id = u.Id?.ToString() ?? string.Empty,
                Name = u.Name ?? string.Empty
            }).ToList());
        }
        // The factory's SSRF guard refused the URL, and the page shows its reason.
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to get users from source server");
            return BadRequest("Could not connect to the server. See the server log.");
        }
    }

    /// <summary>
    /// Lists the top level items of a source library, or its collections or playlists, for the Library Filter picker.
    /// </summary>
    /// <param name="libraryId">The ID of the library on the source.</param>
    /// <param name="serverKey">The key of the server entry to browse, which defaults to the first source.</param>
    /// <param name="search">Narrows the list to names that match, when given.</param>
    /// <param name="startIndex">The index of the first item of the page.</param>
    /// <param name="limit">The most items one page holds, clamped to 200.</param>
    /// <param name="skip">Alias for <paramref name="startIndex"/>, used when present.</param>
    /// <param name="take">Alias for <paramref name="limit"/>, used when present.</param>
    /// <param name="collections">Whether to browse the server's collections instead of a library.</param>
    /// <param name="playlists">Whether to browse the server's playlists instead of a library.</param>
    /// <param name="cancellationToken">Cancels the calls to the other server.</param>
    /// <returns>A page of top level items, or 400 with the reason.</returns>
    [HttpGet("SourceLibraryItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SourceLibraryItemsResponse>> GetSourceLibraryItems(
        [FromQuery] string libraryId,
        [FromQuery] string? serverKey = null,
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
            return BadRequest("Select a library.");
        }

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

        var server = _configManager.Configuration.ResolveServer(serverKey);
        if (server is null || !server.IsConfigured)
        {
            return BadRequest("The server is not configured.");
        }

        if (!collections && !playlists && !Guid.TryParse(libraryId, out _))
        {
            return BadRequest("The library ID is not valid.");
        }

        try
        {
            using var client = _clientFactory.Create(server);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get source files for library {LibraryId}", SanitizeForLog(libraryId));
            return BadRequest("Could not load the items from the server.");
        }
    }

    /// <summary>
    /// Checks a Server URL with the SSRF guard the client factory enforces and returns it normalized, keeping any sub path a reverse proxy serves Jellyfin at.
    /// A name is resolved here, so a refused name is reported when the entry is saved.
    /// </summary>
    private static async Task<ValidateUrlResponse> ValidateServerUrlAsync(string url, bool allowPrivateNetwork, CancellationToken cancellationToken)
    {
        var ssrfError = await ConfigurationUtilities.ValidateServerUrlForSsrfAsync(url, allowPrivateNetwork, cancellationToken).ConfigureAwait(false);
        if (ssrfError != null)
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = ssrfError
            };
        }

        var uri = new Uri(url, UriKind.Absolute);

        var isLocalhost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                          uri.Host.Equals("::1", StringComparison.Ordinal);

        var normalizedUrl = $"{uri.Scheme}://{uri.Host}";
        if (!uri.IsDefaultPort)
        {
            normalizedUrl += $":{uri.Port}";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path))
        {
            normalizedUrl += path;
        }

        return new ValidateUrlResponse
        {
            IsValid = true,
            NormalizedUrl = normalizedUrl,
            Message = isLocalhost ? "This URL uses localhost. Make sure that is intended." : null
        };
    }
}
