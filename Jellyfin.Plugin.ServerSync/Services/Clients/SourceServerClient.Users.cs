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
/// Users, their profile images, and item image details.
/// </summary>
public partial class SourceServerClient
{
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
            _logger.LogWarning(ex, "Source server {Status} getting user details for {UserId}. Refresh will skip pruning to protect tracking rows", ex.ResponseStatusCode, userId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user details for {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's profile image as a stream. Only a 404 answers null. Every other failure throws,
    /// because callers read null as "the source removed this image" and clear the local one, so a
    /// passing 503 would otherwise delete a profile picture.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image stream, or null when the source answers that the user has no image.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the image could not be read.</exception>
    public async Task<Stream?> GetUserImageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage? response = null;
        var ownershipTransferred = false;
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
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var wrapper = new ResponseDisposingStream(stream, response);
            ownershipTransferred = true;
            return wrapper;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get profile image for user {UserId}", userId);
            throw new InvalidOperationException($"Failed to get profile image for user {userId}: {ex.Message}", ex);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                DisposeQuiet(response);
            }
        }
    }

    /// <summary>
    /// Gets both the SHA256 hash and content length of a user's profile image in a single download.
    /// This avoids the overhead of separate GET (for hash) and HEAD (for size) requests.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (hash, size), or (null, null) when the source answers that the user has no image.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the image could not be read, so a failure is never taken for a removed image.</exception>
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
        catch (InvalidOperationException)
        {
            // GetUserImageAsync already logged and wrapped the cause.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get profile image hash and size for user {UserId}", userId);
            throw new InvalidOperationException($"Failed to read profile image for user {userId}: {ex.Message}", ex);
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
            // Warning, not Debug: without sizes the manifest stays tag-only,
            // image comparison degrades to count-only, and genuine image
            // changes stop being detected. A 403 here (non-admin token) is
            // silent otherwise.
            _logger.LogWarning(ex, "Failed to get image info for item {ItemId}. Image sizes will be unavailable and image changes may go undetected", itemId);
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

        // Ownership flips to the returned stream wrapper only on the success
        // path. The finally cleans up every other exit. The previous
        // per-branch `response.Dispose(); request.Dispose();` pairs leaked the
        // request whenever the first disposal (or the log call before it)
        // threw.
        var ownershipTransferred = false;
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
                return (null, null);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return (wrapper, contentType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bumped from LogDebug, failures here were invisible at the
            // default Jellyfin log level and made "verify says local empty,
            // source non-empty" failures impossible to root-cause without
            // turning on plugin debug logs.
            _logger.LogWarning(ex, "Failed to download image {ImageType}/{Index} for item {ItemId}", imageType, imageIndex, itemId);
            return (null, null);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                DisposeQuiet(response);
                DisposeQuiet(request);
            }
        }
    }
}
