using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Settles the rows a full scan queued against peers that carry versions. For a Both peer the scan is
/// no longer "source wins": a row whose value was edited here more recently than on the peer stays as
/// it is, marked synced with the reason, and a peer this server sends to is told to pull this
/// server's value. Any peer that carries versions is asked, whatever its mode, since the question is
/// about the value and not the direction. Rows from peers that predate hints keep today's rule and
/// stay queued.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ScanConflictSettler
{
    private readonly VersionConflictResolver _resolver;
    private readonly LocalHintPublisher _publisher;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanConflictSettler"/> class.
    /// </summary>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="publisher">Publishes this server's winning values to the peers.</param>
    public ScanConflictSettler(VersionConflictResolver resolver, LocalHintPublisher publisher)
    {
        _resolver = resolver;
        _publisher = publisher;
    }

    /// <summary>Settles one module's queued rows.</summary>
    /// <typeparam name="TRecord">The row type.</typeparam>
    /// <param name="queued">The rows the scan queued.</param>
    /// <param name="sources">The connected peers.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="localKeyOf">This server's key for a row.</param>
    /// <param name="peerKeyOf">The peer's key for a row.</param>
    /// <param name="localObjectOf">The local item id and the path or name needed to publish a hint.</param>
    /// <param name="store">Stores a row changed here.</param>
    /// <param name="logger">The calling task's logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SettleAsync<TRecord>(
        IList<TRecord> queued,
        IReadOnlyList<ScanSource> sources,
        HintKind kind,
        Func<TRecord, string> localKeyOf,
        Func<TRecord, string> peerKeyOf,
        Func<TRecord, (string? LocalId, string? PathOrName)> localObjectOf,
        Action<TRecord> store,
        ILogger logger,
        CancellationToken cancellationToken)
        where TRecord : SyncRecord
    {
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(localKeyOf);
        ArgumentNullException.ThrowIfNull(peerKeyOf);
        ArgumentNullException.ThrowIfNull(localObjectOf);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var source in sources)
        {
            var rows = queued.Where(r => string.Equals(r.ServerKey, source.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0 || !await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var peerVersions = await _resolver.PeerVersionsAsync(source, kind, rows.Select(peerKeyOf).Distinct(StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false);
            if (peerVersions is null)
            {
                continue;
            }

            var kept = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (localId, pathOrName) = localObjectOf(row);
                var localKey = localKeyOf(row);
                if (string.IsNullOrEmpty(localKey) || !Guid.TryParse(localId, out var localGuid))
                {
                    continue;
                }

                peerVersions.TryGetValue(peerKeyOf(row), out var peerVersion);
                if (_resolver.Decide(kind, localKey, peerVersion) != VersionDecision.Keep)
                {
                    continue;
                }

                row.Status = SyncStatus.Synced;
                row.StatusDate = DateTime.UtcNow;
                row.Reason = source.Server.Pushes
                    ? $"kept: this server's edit is newer than the one on '{source.Name}', which will pull it"
                    : $"kept: this server's edit is newer than the one on '{source.Name}'";
                store(row);
                kept++;

                if (!source.Server.Pushes)
                {
                    continue;
                }

                var version = _resolver.Recorded(kind, localKey);
                if (version is null)
                {
                    continue;
                }

                if (kind == HintKind.Metadata && !string.IsNullOrEmpty(pathOrName))
                {
                    _publisher.PublishMetadata(localGuid, pathOrName, version, excludePeerKey: null);
                }
                else if (kind == HintKind.People && !string.IsNullOrEmpty(pathOrName))
                {
                    _publisher.PublishPeople(pathOrName, localGuid, version, excludePeerKey: null);
                }
            }

            if (kept > 0)
            {
                logger.LogInformation("Kept {Count} local {Kind} value(s) that are newer than '{Peer}' and told it to pull them", kept, kind, source.Name);
            }
        }
    }
}
