using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// What an apply task does around a write for the hint pipeline: registers the write with the apply
/// guard, and afterwards records the version the copied value carries, which is the peer's own, so a
/// copy never looks newer than the edit it came from. A value the peer never recorded an edit for
/// leaves no version here either.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class AppliedVersionRecorder
{
    private readonly ApplyGuard _guard;
    private readonly VersionConflictResolver _resolver;
    private readonly ILogger<AppliedVersionRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppliedVersionRecorder"/> class.
    /// </summary>
    /// <param name="guard">The apply guard.</param>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="logger">Logger.</param>
    public AppliedVersionRecorder(ApplyGuard guard, VersionConflictResolver resolver, ILogger<AppliedVersionRecorder> logger)
    {
        _guard = guard;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>Registers a write with the guard.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>A handle to dispose when the write is done, or null when there is no key.</returns>
    public IDisposable? Enter(HintKind kind, string localKey)
        => string.IsNullOrEmpty(localKey) ? null : _guard.Enter(HintProtocol.GuardKey(kind, localKey));

    /// <summary>Registers a write that may touch any object of a kind as a side effect.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>A handle to dispose when the write is done.</returns>
    public IDisposable EnterAll(HintKind kind) => _guard.Enter(HintProtocol.GuardAllKey(kind));

    /// <summary>
    /// Records the version that belongs to a value just copied from a peer. Metadata and people carry the
    /// version the scan read together with the value, kept pending until now, so no request is made per
    /// row and a peer edit made in between cannot stamp the older value with its newer version. User
    /// settings never settle in the scan, so their version is still read from the peer here. There are
    /// few of them.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <param name="peerKey">The peer's key for the object.</param>
    /// <param name="source">The peer the value came from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecordAsync(HintKind kind, string localKey, string peerKey, ScanSource? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(localKey) || source is null)
        {
            return;
        }

        if (_resolver.PromotePending(kind, localKey) || kind != HintKind.Users)
        {
            return;
        }

        try
        {
            if (await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false) != true)
            {
                return;
            }

            var versions = await _resolver.PeerVersionsAsync(source, kind, new[] { peerKey }, cancellationToken).ConfigureAwait(false);

            // Only a version the peer recorded is carried over. A value the peer never edited by hand
            // leaves none here either, so the next scan from that source still wins over it and a hand
            // made edit anywhere beats it.
            if (versions is null || !versions.TryGetValue(peerKey, out var peerVersion))
            {
                return;
            }

            _resolver.Record(new ObjectVersion { Kind = kind, Key = localKey, ServerId = peerVersion.ServerId, Timestamp = peerVersion.Timestamp });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record the version of a {Kind} value copied from '{Peer}'", kind, source.Name);
        }
    }
}
