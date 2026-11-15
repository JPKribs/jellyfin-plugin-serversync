using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Proxies source-server images for the admin pages. The decrypted source API key never appears in an
/// image URL, so it never reaches browser devtools, caches, or the source server's access logs. The key
/// stays server-side and the browser authenticates with its own local session.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Streams an item or user primary image from the source server.
    /// </summary>
    /// <param name="itemId">Source item (or user) ID.</param>
    /// <param name="user">True to fetch a user profile image instead of an item image.</param>
    /// <param name="maxHeight">Maximum image height in pixels.</param>
    /// <param name="serverKey">The server entry the image lives on. Defaults to the first scan server.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The image bytes, or 404 when the server or image is unavailable.</returns>
    [HttpGet("ImageProxy")]
    public async Task<ActionResult> GetSourceImage(
        [FromQuery] Guid itemId,
        [FromQuery] bool user = false,
        [FromQuery] int maxHeight = 80,
        [FromQuery] string? serverKey = null,
        CancellationToken cancellationToken = default)
    {
        var server = _configManager.Configuration.ResolveServer(serverKey);
        if (server is null || !server.IsConfigured)
        {
            return NotFound();
        }

        if (await Utilities.ConfigurationUtilities.ValidateServerUrlForSsrfAsync(server.Url, server.AllowPrivateNetwork, cancellationToken).ConfigureAwait(false) != null)
        {
            return NotFound();
        }

        maxHeight = Math.Clamp(maxHeight, 1, 600);

        try
        {
            using var client = _clientFactory.Create(server);
            using var response = await client.OpenPrimaryImageAsync(itemId, user, maxHeight, cancellationToken).ConfigureAwait(false);
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
