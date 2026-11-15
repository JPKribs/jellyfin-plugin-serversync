using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Gives each item read from a peer that is one of the peer's links the path its copy would have, so callers match the file the link stands in for.
/// Only a <c>.strm</c> the peer lists as a link is changed, and the list is read once a <c>.strm</c> turns up.
/// </summary>
public partial class SourceServerClient
{
    private readonly SemaphoreSlim _peerLinksLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _peerLinkIds = new();
    private Dictionary<string, string>? _peerLinks;
    private DateTime _peerLinksFailedAt;

    /// <summary>
    /// How long a failed read of the peer's links is trusted before asking again.
    /// </summary>
    private static readonly TimeSpan PeerLinksRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Tells whether an item read from the peer is one of the peer's links, and so not a file to copy.
    /// </summary>
    /// <param name="itemId">The peer's id of the item.</param>
    /// <returns><c>true</c> for a link.</returns>
    public bool IsPeerLink(Guid? itemId) => itemId is { } id && _peerLinkIds.ContainsKey(id);

    /// <summary>
    /// Gets a value indicating whether the peer's links could not be read, in which case Content leaves the peer's <c>.strm</c> files alone.
    /// </summary>
    public bool PeerLinksUnknown { get; private set; }

    /// <summary>
    /// Applies the peer's links to a page of items and returns the same page.
    /// </summary>
    private async Task<BaseItemDtoQueryResult?> WithPeerLinksAsync(BaseItemDtoQueryResult? result, CancellationToken cancellationToken)
    {
        await ApplyPeerLinksAsync(result?.Items, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Replaces the path of each item that is one of the peer's links with the path its copy would have.
    /// </summary>
    private async Task ApplyPeerLinksAsync(IEnumerable<BaseItemDto>? items, CancellationToken cancellationToken)
    {
        var shortcuts = items?.Where(item => item?.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (shortcuts is not { Count: > 0 })
        {
            return;
        }

        var links = await PeerLinksAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in shortcuts)
        {
            if (links.TryGetValue(item.Path!, out var copied))
            {
                item.Path = copied;
                if (item.Id is { } id)
                {
                    _peerLinkIds[id] = 0;
                }
            }
        }
    }

    private readonly SemaphoreSlim _peerCopiesLock = new(1, 1);
    private Dictionary<string, string>? _peerCopies;

    /// <summary>
    /// Gets the origin of a file on the peer, reading the peer's copies once for this client.
    /// A failed read throws, since a file of unknown origin cannot be told from the peer's own.
    /// </summary>
    /// <param name="peerPath">The file's path on the peer.</param>
    /// <param name="cancellationToken">Cancels the read of the peer's copies.</param>
    /// <returns>The origin server's id, or null for the peer's own file or a peer with no copies route.</returns>
    public virtual async Task<string?> OriginOfAsync(string? peerPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(peerPath))
        {
            return null;
        }

        if (_peerCopies is null)
        {
            await _peerCopiesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_peerCopies is null)
                {
                    var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var copy in (await GetPeerCopiesAsync(cancellationToken).ConfigureAwait(false))?.Copies ?? new List<Models.Peer.PeerCopy>())
                    {
                        if (!string.IsNullOrEmpty(copy.Path) && !string.IsNullOrEmpty(copy.OriginServerId))
                        {
                            copies[copy.Path] = copy.OriginServerId;
                        }
                    }

                    _peerCopies = copies;
                }
            }
            finally
            {
                _peerCopiesLock.Release();
            }
        }

        return _peerCopies.TryGetValue(peerPath, out var origin) ? origin : null;
    }

    /// <summary>
    /// Gets the peer's links by link path, reading them once and asking again a minute after a failed read.
    /// </summary>
    private async Task<Dictionary<string, string>> PeerLinksAsync(CancellationToken cancellationToken)
    {
        if (_peerLinks is { } known)
        {
            return known;
        }

        await _peerLinksLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_peerLinks is null && DateTime.UtcNow - _peerLinksFailedAt >= PeerLinksRetry)
            {
                var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var link in (await GetPeerLinksAsync(cancellationToken).ConfigureAwait(false))?.Links ?? new List<Models.Peer.PeerLink>())
                    {
                        if (!string.IsNullOrEmpty(link.LinkPath) && !string.IsNullOrEmpty(link.CopiedPath))
                        {
                            links[link.LinkPath] = link.CopiedPath;
                        }
                    }
                }
                // A failed read is no answer, so nothing is kept and the peer is asked again.
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Could not read the peer's links, so its .strm files wait until it answers");
                    _peerLinksFailedAt = DateTime.UtcNow;
                    PeerLinksUnknown = true;
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                _peerLinks = links;
                PeerLinksUnknown = false;
            }

            return _peerLinks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _peerLinksLock.Release();
        }
    }
}
