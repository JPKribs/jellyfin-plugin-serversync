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
/// Settles the rows a full scan queued against peers that carry versions. For a Sync server the scan
/// does not simply take the source's value: a row whose value was edited here more recently than on
/// the peer stays as it is, marked synced with the reason, and a peer this server sends to is told to
/// pull this server's value. Any peer that carries versions is asked, whatever its direction, since
/// the question is about the value and not the direction. Rows from peers that carry no versions stay
/// queued and take the source's value.
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

    /// <summary>The connected servers that keep versions, so their offers can be weighed on who edited last.</summary>
    /// <param name="sources">The connected servers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entry keys of the servers that keep versions.</returns>
    public async Task<HashSet<string>> ServersWithVersionsAsync(IReadOnlyList<ScanSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var versioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false) == true)
            {
                versioned.Add(source.Key);
            }
        }

        return versioned;
    }

    /// <summary>
    /// Chooses, for each thing several servers offer, the server that holds the newest recorded edit of
    /// it. Rank still decides whenever nothing says otherwise: when the highest ranked server offering
    /// it keeps no versions, when no server has recorded an edit of it, when two servers hold the same
    /// edit, and when a server's versions cannot be read. A lower ranked server is only heard over the
    /// ones ranked above it that keep versions too.
    /// </summary>
    /// <typeparam name="TSource">The offer type.</typeparam>
    /// <param name="contested">The offers of each contested key, highest ranked first.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="versioned">The servers that keep versions, as <see cref="ServersWithVersionsAsync"/> gives them.</param>
    /// <param name="sourceOf">The server an offer came from.</param>
    /// <param name="peerKeyOf">The key the offering server knows the thing by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The winning offer for each key rank does not decide.</returns>
    public async Task<IReadOnlyDictionary<string, TSource>> NewestOffersAsync<TSource>(
        IReadOnlyDictionary<string, IReadOnlyList<TSource>> contested,
        HintKind kind,
        HashSet<string> versioned,
        Func<TSource, ScanSource> sourceOf,
        Func<TSource, string?> peerKeyOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contested);
        ArgumentNullException.ThrowIfNull(versioned);
        ArgumentNullException.ThrowIfNull(sourceOf);
        ArgumentNullException.ThrowIfNull(peerKeyOf);
        var winners = new Dictionary<string, TSource>(StringComparer.OrdinalIgnoreCase);

        // Only the offers that can be weighed: the run of servers from the top of each list that keep versions.
        var weighed = new Dictionary<string, List<TSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, offers) in contested)
        {
            var run = offers.TakeWhile(o => versioned.Contains(sourceOf(o).Key)).ToList();
            if (run.Count > 1)
            {
                weighed[key] = run;
            }
        }

        if (weighed.Count == 0)
        {
            return winners;
        }

        var versions = new Dictionary<string, Dictionary<string, ObjectVersion>>(StringComparer.OrdinalIgnoreCase);
        foreach (var byServer in weighed.Values.SelectMany(o => o).GroupBy(o => sourceOf(o).Key, StringComparer.OrdinalIgnoreCase))
        {
            var source = sourceOf(byServer.First());
            var keys = byServer.Select(peerKeyOf).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
            var read = await _resolver.PeerVersionsAsync(source, kind, keys, cancellationToken).ConfigureAwait(false);
            if (read is not null)
            {
                versions[source.Key] = read;
            }
        }

        foreach (var (key, offers) in weighed)
        {
            // A server whose versions could not be read cannot be weighed, so rank decides this key.
            if (offers.Any(o => !versions.ContainsKey(sourceOf(o).Key)))
            {
                continue;
            }

            var best = offers[0];
            var bestVersion = VersionOf(best);
            foreach (var offer in offers.Skip(1))
            {
                var version = VersionOf(offer);
                if (version is null)
                {
                    continue;
                }

                if (bestVersion is null || (!VersionDecider.SameVersion(version, bestVersion) && VersionDecider.IsNewer(version, bestVersion)))
                {
                    best = offer;
                    bestVersion = version;
                }
            }

            if (!ReferenceEquals(best, offers[0]))
            {
                winners[key] = best;
            }
        }

        return winners;

        ObjectVersion? VersionOf(TSource offer)
            => peerKeyOf(offer) is { Length: > 0 } peerKey && versions[sourceOf(offer).Key].TryGetValue(peerKey, out var version) ? version : null;
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
