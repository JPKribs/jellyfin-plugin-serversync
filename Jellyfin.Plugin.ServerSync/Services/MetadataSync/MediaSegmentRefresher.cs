using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.MetadataSync;

/// <summary>
/// Reads the media segments of every video and audio file the Metadata table matched, in batches, from the highest ranked connected source that holds each,
/// and keeps them for the media segment provider with the server they were first made on. A source that cannot answer leaves what was kept for its files as it was,
/// and segments first made on this server are never taken back.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class MediaSegmentRefresher
{
    private readonly MediaSegmentStore _store;
    private readonly ILogger<MediaSegmentRefresher> _logger;
    private readonly string _thisServerId;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentRefresher"/> class.
    /// </summary>
    /// <param name="store">Lists the matched files and keeps their segments.</param>
    /// <param name="applicationHost">Gives this server's id, so its own segments are never taken back.</param>
    /// <param name="logger">Writes what each refresh read.</param>
    /// <param name="time">Stamps when segments were read, or null for the system clock.</param>
    public MediaSegmentRefresher(MediaSegmentStore store, MediaBrowser.Controller.IServerApplicationHost applicationHost, ILogger<MediaSegmentRefresher> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _thisServerId = applicationHost.SystemId;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads and keeps the segments of every matched file, then forgets those of files no longer matched.
    /// </summary>
    /// <param name="sources">The connected sources, in rank order.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>How many files' segments were kept, and how many were forgotten.</returns>
    public async Task<(int Kept, int Forgotten)> RefreshAsync(IReadOnlyList<ScanSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var now = _time.GetUtcNow().UtcDateTime;
        var kept = 0;
        var forgotten = 0;
        foreach (var (source, files) in Assign(_store.Candidates(), sources))
        {
            // One source item may stand for several local items, such as two libraries mapping one folder.
            var bySourceItem = files.GroupBy(f => f.SourceItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var batch in bySourceItem.Keys.Chunk(PeerSegmentsRequest.MaxItems))
            {
                PeerSegmentsResponse answer;
                try
                {
                    answer = await source.Client.GetPeerSegmentsAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Could not read media segments from '{Server}', so the ones kept for its files stay as they were: {Reason}", source.Server.DisplayName, FormatUtilities.SanitizeForLog(ex.Message, 300));
                    break;
                }

                var save = new List<SourceMediaSegments>();
                var gone = new List<string>();
                foreach (var item in answer.Items)
                {
                    if (item is null || !bySourceItem.TryGetValue(item.ItemId ?? string.Empty, out var locals))
                    {
                        continue;
                    }

                    var origin = OriginOf(item, source);

                    // Segments first made here came back through another server, so this server's own are kept.
                    if (!item.Found || string.Equals(origin, _thisServerId, StringComparison.OrdinalIgnoreCase))
                    {
                        gone.AddRange(locals.Select(l => l.LocalItemId));
                        continue;
                    }

                    save.AddRange(locals.Select(l => new SourceMediaSegments(l.LocalItemId, source.Server.Key, l.SourceItemId, item.Segments ?? new List<PeerSegment>(), item.Size, item.RunTimeTicks, now, origin)));
                }

                _store.Save(save);
                kept += save.Count;
                forgotten += _store.Remove(gone);
            }
        }

        forgotten += _store.RemoveUntracked();
        _logger.LogInformation("Kept the media segments of {Kept} files from the servers this one pulls from, and forgot {Forgotten}", kept, forgotten);
        return (kept, forgotten);
    }

    /// <summary>
    /// Gives the server a file's segments were first made on: the one the source names, or the source itself when it names none.
    /// </summary>
    /// <param name="item">The source's answer for the file.</param>
    /// <param name="source">The source that answered.</param>
    /// <returns>The origin's Jellyfin id, or null when neither is known.</returns>
    public static string? OriginOf(PeerSegmentsItem item, ScanSource source)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        return !string.IsNullOrWhiteSpace(item.OriginServerId) ? item.OriginServerId
            : !string.IsNullOrWhiteSpace(source.Server.ServerId) ? source.Server.ServerId
            : null;
    }

    /// <summary>
    /// Gives each local file to the highest ranked connected source that holds it.
    /// </summary>
    /// <param name="candidates">Each source that holds each file.</param>
    /// <param name="sources">The connected sources, in rank order.</param>
    /// <returns>The files each source answers for, in rank order, leaving out sources with none.</returns>
    public static IReadOnlyList<(ScanSource Source, IReadOnlyList<MediaSegmentCandidate> Files)> Assign(IReadOnlyList<MediaSegmentCandidate> candidates, IReadOnlyList<ScanSource> sources)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(sources);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            rank.TryAdd(sources[i].Server.Key, i);
        }

        var chosen = candidates
            .Where(c => rank.ContainsKey(c.ServerKey))
            .GroupBy(c => c.LocalItemId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => rank[c.ServerKey]).First())
            .ToList();
        return sources
            .Select(s => (Source: s, Files: (IReadOnlyList<MediaSegmentCandidate>)chosen.Where(c => string.Equals(c.ServerKey, s.Server.Key, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(p => p.Files.Count > 0)
            .ToList();
    }
}
