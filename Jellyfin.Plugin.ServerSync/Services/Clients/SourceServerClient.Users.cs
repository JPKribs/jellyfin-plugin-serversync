using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Reads users, their profile images, and item images from the source.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>
    /// Gets a source user's full details, including policy and configuration.
    /// </summary>
    /// <param name="userId">The source user to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The user, or null when the request failed without an error status.</returns>
    /// <exception cref="Microsoft.Kiota.Abstractions.ApiException">Thrown when the source answers with an error status, so the Users scan skips pruning.</exception>
    public async Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Users[userId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not Microsoft.Kiota.Abstractions.ApiException { ResponseStatusCode: >= 400 })
        {
            _logger.LogError(ex, "Failed to get user details for {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Opens a source user's profile image as a stream.
    /// Only a 404 answers null and every other failure throws, since callers clear the image here on null.
    /// </summary>
    /// <param name="userId">The source user whose image is read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The image stream, or null when the source answers that the user has no image.</returns>
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
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var wrapper = new ResponseDisposingStream(stream, response);
            ownershipTransferred = true;
            return wrapper;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
    /// Gets the hash and the size of a source user's profile image from one download.
    /// </summary>
    /// <param name="userId">The source user whose image is read.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The hash and the size in bytes, or two nulls when the source answers that the user has no image.</returns>
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

            var (hash, totalBytes) = await HashUtilities.ComputeSha256HashAsync(imageStream, cancellationToken).ConfigureAwait(false);
            return (hash, totalBytes);
        }
        // The image opener already wraps its failure in an InvalidOperationException.
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to read profile image for user {userId}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lists a source item's images with their sizes and dimensions.
    /// </summary>
    /// <param name="itemId">The source item whose images are listed.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The images, or null when the request failed, in which case image changes may go unseen.</returns>
    public async Task<List<ImageInfo>?> GetItemImageInfoAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items[itemId].Images.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get image info for item {ItemId}. Image sizes will be unavailable and image changes may go undetected", itemId);
            return null;
        }
    }

    /// <summary>
    /// Reads an image's size from a HEAD request, since the source's <see cref="ImageInfo.Size"/> goes stale when the file is replaced on disk.
    /// </summary>
    /// <param name="itemId">The source item the image belongs to.</param>
    /// <param name="imageType">The image type, such as Primary.</param>
    /// <param name="imageIndex">The image's index, or null for the first.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The size in bytes, or null when it cannot be read.</returns>
    public async Task<long?> GetItemImageContentLengthAsync(
        Guid itemId,
        string imageType,
        int? imageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageType);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "HEAD for image size failed: {ImageType}/{Index} on {ItemId}", FormatUtilities.SanitizeForLog(imageType), imageIndex, itemId);
            return null;
        }
    }

    /// <summary>
    /// Opens a source item's image as a stream that also disposes the request and response.
    /// </summary>
    /// <param name="itemId">The source item the image belongs to.</param>
    /// <param name="imageType">The image type, such as Primary or Backdrop.</param>
    /// <param name="imageIndex">The image's index for a type that holds several, or null for the first.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The stream and its content type, or two nulls on failure.</returns>
    public async Task<(Stream? Stream, string? ContentType)> DownloadItemImageAsync(
        Guid itemId,
        string imageType,
        int? imageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageType);

        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;
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
                    FormatUtilities.SanitizeForLog(imageType), imageIndex, itemId, response.StatusCode);
                return (null, null);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var wrapper = new ResponseDisposingStream(stream, response, request);
            ownershipTransferred = true;
            return (wrapper, contentType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download image {ImageType}/{Index} for item {ItemId}", FormatUtilities.SanitizeForLog(imageType), imageIndex, itemId);
            return (null, null);
        }
        finally
        {
            // The returned stream owns the request and response once it is handed out.
            if (!ownershipTransferred)
            {
                DisposeQuiet(response);
                DisposeQuiet(request);
            }
        }
    }

    /// <summary>
    /// Opens the primary image of an item or a user on the source, scaled to a height. The caller disposes the response.
    /// </summary>
    /// <param name="id">The source's id of the item or the user.</param>
    /// <param name="user">Whether the id names a user.</param>
    /// <param name="maxHeight">The tallest image to ask for, in pixels.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The source's response, with only its headers read.</returns>
    public async Task<HttpResponseMessage> OpenPrimaryImageAsync(Guid id, bool user, int maxHeight, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _serverUrl + (user ? "/Users/" : "/Items/") + id.ToString() + "/Images/Primary?maxHeight=" + maxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddAuthorizationHeader(request);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }
}
