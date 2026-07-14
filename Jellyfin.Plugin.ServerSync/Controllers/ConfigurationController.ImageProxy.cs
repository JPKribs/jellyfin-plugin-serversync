using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Proxies source-server images for the admin pages. The pages used to embed
/// the decrypted source API key in every image URL, which leaked it into
/// browser devtools, caches, and the source server's access logs; this keeps
/// the key server-side and lets the browser authenticate with its own local
/// session instead.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Streams an item or user primary image from the source server.
    /// </summary>
    /// <param name="itemId">Source item (or user) ID.</param>
    /// <param name="user">True to fetch a user profile image instead of an item image.</param>
    /// <param name="maxHeight">Maximum image height in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("ImageProxy")]
    public async Task<ActionResult> GetSourceImage(
        [FromQuery] Guid itemId,
        [FromQuery] bool user = false,
        [FromQuery] int maxHeight = 80,
        CancellationToken cancellationToken = default)
    {
        var config = _configManager.Configuration;
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            return NotFound();
        }

        // Same SSRF gate every other source-server call path goes through
        // (the factory validates too, but this endpoint bypasses the factory).
        if (Utilities.ConfigurationUtilities.ValidateServerUrlForSsrf(config.SourceServerUrl) != null)
        {
            return NotFound();
        }

        maxHeight = Math.Clamp(maxHeight, 1, 600);
        var baseUrl = config.SourceServerUrl.TrimEnd('/');
        var url = user
            ? $"{baseUrl}/Users/{itemId}/Images/Primary?maxHeight={maxHeight}"
            : $"{baseUrl}/Items/{itemId}/Images/Primary?maxHeight={maxHeight}";

        try
        {
            var client = _httpClientFactory.CreateClient(SourceServerClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "MediaBrowser", $"Token=\"{_configManager.DecryptedSourceServerApiKey}\"");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            // Only serve images: anything else from a hostile "source server"
            // would execute in this server's origin if opened directly.
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound();
            }

            if (response.Content.Headers.ContentLength is > MaxImageBytes)
            {
                return NotFound();
            }

            var bytes = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bytes == null)
            {
                return NotFound();
            }

            // Cacheable: the page fetches these with header auth, so the URL
            // carries no credentials and caching by URL is safe.
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(bytes, contentType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            _logger.LogDebug(ex, "Image proxy fetch failed for {ItemId}", itemId);
            return NotFound();
        }
    }

    /// <summary>
    /// Upper bound for a proxied thumbnail (maxHeight is clamped to 600, so
    /// anything past this is not a real image answer).
    /// </summary>
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (buffer.Length > MaxImageBytes)
                {
                    return null;
                }
            }

            return buffer.ToArray();
        }
    }
}
