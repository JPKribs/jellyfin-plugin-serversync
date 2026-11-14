using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// A peer's links. When the peer holds a link, its Jellyfin reports the .strm path, which matches
/// nothing here. A peer running Server Sync lists its links with the path each full copy would have,
/// and items read from it are given that path instead, so every caller matches the file the link
/// stands in for. Nothing is guessed from a .strm's name: a .strm the peer does not list as a link
/// keeps its path, and the list is only asked for once a .strm turns up.
/// </summary>
public partial class SourceServerClient
{
    private readonly SemaphoreSlim _peerLinksLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _peerLinkIds = new();
    private Dictionary<string, string>? _peerLinks;
    private DateTime _peerLinksFailedAt;

    /// <summary>How long a failed read of the peer's links is trusted before asking again.</summary>
    private static readonly TimeSpan PeerLinksRetry = TimeSpan.FromMinutes(1);

    /// <summary>Whether an item read from the peer is one of the peer's links, and so not a file to copy.</summary>
    /// <param name="itemId">The peer's id of the item.</param>
    /// <returns>True for a link.</returns>
    public bool IsPeerLink(Guid? itemId) => itemId is { } id && _peerLinkIds.ContainsKey(id);

    /// <summary>
    /// Whether the peer's links could not be read, so a .strm from it may or may not be one of its links.
    /// Content leaves such files alone until the peer answers.
    /// </summary>
    public bool PeerLinksUnknown { get; private set; }

    private async Task<BaseItemDtoQueryResult?> WithPeerLinksAsync(BaseItemDtoQueryResult? result, CancellationToken cancellationToken)
    {
        await ApplyPeerLinksAsync(result?.Items, cancellationToken).ConfigureAwait(false);
        return result;
    }

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
    /// The id of the server a file on the peer first came from, when the peer downloaded it from
    /// another server. Null for a file that is the peer's own. The peer's copies are read once for this
    /// client. A peer with no copies route, as an older version is, names no origin. A read that fails
    /// throws, since a file whose origin is not known cannot be told from the peer's own.
    /// </summary>
    /// <param name="peerPath">The file's path on the peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The origin server's id, or null.</returns>
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Not cached: a failed read is not an answer, so it is asked again shortly, and until
                    // then this peer's .strm files are neither matched as links nor copied as content.
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
