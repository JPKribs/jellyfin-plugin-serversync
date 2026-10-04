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
            if (rows.Count == 0)
            {
                continue;
            }

            // A peer that answers it keeps no versions is scanned the way it always was, the source
            // winning. One that could not be asked, or whose versions could not be read, is not taken to
            // have none: its rows that would overwrite an edit recorded here wait for the next scan.
            var carries = await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false);
            if (carries == false)
            {
                continue;
            }

            var peerVersions = carries == true
                ? await _resolver.PeerVersionsAsync(source, kind, rows.Select(peerKeyOf).Distinct(StringComparer.Ordinal).ToList(), cancellationToken).ConfigureAwait(false)
                : null;
            if (peerVersions is null)
            {
                Hold(rows, source, kind, localKeyOf, store, logger);
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
                    // The value was read together with this version, so the version is recorded once the
                    // value applies, rather than whatever the peer holds by then.
                    if (peerVersion is null)
                    {
                        _resolver.ClearPending(kind, localKey);
                    }
                    else
                    {
                        _resolver.RecordPending(new ObjectVersion { Kind = kind, Key = localKey, ServerId = peerVersion.ServerId, Timestamp = peerVersion.Timestamp });
                    }

                    continue;
                }

                _resolver.ClearPending(kind, localKey);
                row.MarkKept($"kept: this server's edit is newer than the one on '{source.Name}'");
                store(row);
                kept++;

                if (!source.Server.Pushes)
                {
                    continue;
                }

                // Only an edit made here is announced from here. A newer value this server holds from a
                // third server is that server's to announce, and repeating it would loop between peers.
                var version = _resolver.Recorded(kind, localKey);
                if (version is null || !_resolver.IsThisServer(version))
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
                logger.LogInformation("Kept {Count} local {Kind} value(s) that are newer than '{Peer}'", kept, kind, source.Name);
            }
        }
    }

    // Rows whose local value carries a recorded edit are left alone this scan, since nothing can say
    // whether the peer's value is newer. Their stored hash is not touched, so the next scan queues them
    // again and decides once the versions can be read.
    private void Hold<TRecord>(IList<TRecord> rows, ScanSource source, HintKind kind, Func<TRecord, string> localKeyOf, Action<TRecord> store, ILogger logger)
        where TRecord : SyncRecord
    {
        var held = 0;
        foreach (var row in rows)
        {
            var localKey = localKeyOf(row);
            if (string.IsNullOrEmpty(localKey) || _resolver.Recorded(kind, localKey) is null)
            {
                continue;
            }

            row.MarkKept($"held: this server's value carries an edit and the versions on '{source.Name}' could not be read, so the next scan decides");
            store(row);
            held++;
        }

        if (held > 0)
        {
            logger.LogWarning("Held {Count} {Kind} value(s) that carry an edit here, since the versions on '{Peer}' could not be read. The next scan decides", held, kind, source.Name);
        }
    }
}
