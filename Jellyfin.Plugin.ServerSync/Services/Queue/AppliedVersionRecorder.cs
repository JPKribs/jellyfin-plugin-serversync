using System;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Registers a scheduled task's write with the apply guard, and afterwards records the stamp the copied value carries.
/// The stamp is the peer's own, so a copy never looks newer than the edit it came from.
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
    /// <param name="guard">Marks a write so its save raises no hint.</param>
    /// <param name="resolver">Reads the peer's stamps and records them here.</param>
    /// <param name="logger">Writes a stamp that could not be recorded.</param>
    public AppliedVersionRecorder(ApplyGuard guard, VersionConflictResolver resolver, ILogger<AppliedVersionRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);

        _guard = guard;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>Registers a write with the guard.</summary>
    /// <param name="kind">The sync type of the object being written.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <returns>A handle to dispose when the write is done, or null when there is no key.</returns>
    public IDisposable? Enter(HintKind kind, string localKey)
        => string.IsNullOrEmpty(localKey) ? null : _guard.Enter(HintProtocol.GuardKey(kind, localKey));

    /// <summary>Registers a write that may touch any object of a kind as a side effect.</summary>
    /// <param name="kind">The sync type whose objects the write may touch.</param>
    /// <returns>A handle to dispose when the write is done.</returns>
    public IDisposable EnterAll(HintKind kind) => _guard.Enter(HintProtocol.GuardAllKey(kind));

    /// <summary>
    /// Records the stamp that belongs to a value copied from a peer, using the one the scan read with the value.
    /// User settings never settle in the scan, so their stamp is read from the peer here.
    /// </summary>
    /// <param name="kind">The sync type of the copied value.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <param name="peerKey">The peer's key for the object.</param>
    /// <param name="source">The peer the value came from.</param>
    /// <param name="cancellationToken">Cancels the read from the peer.</param>
    /// <returns>A task that ends when the stamp is recorded.</returns>
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

            // A value the peer never stamped leaves none here, so any edit beats it.
            if (versions is null || !versions.TryGetValue(peerKey, out var peerVersion))
            {
                return;
            }

            _resolver.Record(new ObjectVersion { Kind = kind, Key = localKey, ServerId = peerVersion.ServerId, Timestamp = peerVersion.Timestamp });
        }
        // The value is already copied, so a missing stamp must not fail the row.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not record the version of a {Kind} value copied from '{Peer}'", kind, FormatUtilities.SanitizeForLog(source.Name));
        }
    }
}
