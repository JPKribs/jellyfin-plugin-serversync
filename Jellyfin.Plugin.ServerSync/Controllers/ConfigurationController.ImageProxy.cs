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
/// Proxies a source's images for the dashboard, so its API Key stays on this server and never appears in an image URL.
/// </summary>
public partial class ConfigurationController
{
    /// <summary>
    /// Streams an item's or a user's primary image from a source.
    /// </summary>
    /// <param name="itemId">The ID of the item or user on the source.</param>
    /// <param name="user">Whether to fetch a user's profile image instead of an item's image.</param>
    /// <param name="maxHeight">The most pixels high the image may be, clamped to 600.</param>
    /// <param name="serverKey">The key of the server entry the image lives on, which defaults to the first source.</param>
    /// <param name="cancellationToken">Cancels the fetch from the source.</param>
    /// <returns>The image, or 404 when the server or image is unavailable.</returns>
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

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // Anything but an image would run in this server's origin if opened.
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

            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(bytes, contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or ArgumentException)
        {
            _logger.LogDebug(ex, "Image proxy fetch failed for {ItemId}", itemId);
            return NotFound();
        }
    }

    /// <summary>
    /// The most bytes a proxied image may hold. A larger body is not a real thumbnail.
    /// </summary>
    private const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Reads a body into memory, or returns null once it passes the cap.</summary>
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
