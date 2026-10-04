using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// What an apply task does around a write for the hint pipeline: registers the write with the apply
/// guard, and afterwards records the version the copied value carries. The version is the peer's own
/// when the peer carries versions, so a copy never looks newer than the edit it came from. A peer
/// that carries none gets a version of its id and now, the best that can be known.
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
    /// <param name="alsoAll">A kind whose every object the write may touch as a side effect, or null.</param>
    /// <returns>A handle to dispose when the write is done.</returns>
    public IDisposable Enter(HintKind kind, string localKey, HintKind? alsoAll = null)
    {
        var scopes = new List<IDisposable>(2);
        if (!string.IsNullOrEmpty(localKey))
        {
            scopes.Add(_guard.Enter(HintProtocol.GuardKey(kind, localKey)));
        }

        if (alsoAll.HasValue)
        {
            scopes.Add(_guard.Enter(HintProtocol.GuardAllKey(alsoAll.Value)));
        }

        return new Scopes(scopes);
    }

    /// <summary>Records the version of a value just copied from a peer.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <param name="peerKey">The peer's key for the same object.</param>
    /// <param name="source">The connected peer, or null when unknown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecordAsync(HintKind kind, string localKey, string peerKey, ScanSource? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(localKey) || source is null)
        {
            return;
        }

        try
        {
            ObjectVersion? peerVersion = null;
            if (await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false))
            {
                var versions = await _resolver.PeerVersionsAsync(source, kind, new[] { peerKey }, cancellationToken).ConfigureAwait(false);
                if (versions is not null)
                {
                    versions.TryGetValue(peerKey, out peerVersion);
                }
            }

            _resolver.Record(new ObjectVersion
            {
                Kind = kind,
                Key = localKey,
                ServerId = peerVersion?.ServerId ?? source.Server.ServerId,
                Timestamp = peerVersion?.Timestamp ?? DateTime.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record the version of a {Kind} value copied from '{Peer}'", kind, source.Name);
        }
    }

    private sealed class Scopes : IDisposable
    {
        private readonly List<IDisposable> _scopes;

        public Scopes(List<IDisposable> scopes) => _scopes = scopes;

        public void Dispose()
        {
            foreach (var scope in _scopes)
            {
                scope.Dispose();
            }
        }
    }
}
