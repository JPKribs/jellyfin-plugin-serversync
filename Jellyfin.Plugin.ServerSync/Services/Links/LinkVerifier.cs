using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>What one check of this server's links found.</summary>
public sealed record LinkVerification(int Checked, int Dead, int Restored, int Described);

/// <summary>
/// Checks this server's links against the servers they stream from, as part of every content sync, so
/// a link that can no longer play shows on the Sync tab with the reason before someone presses play.
/// A link the source can stream again is settled again. Links whose item still has no media info are
/// given the source's, which also covers anything the probe missed or a restart dropped.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkVerifier
{
    /// <summary>How a row's reason starts when this check, rather than writing the link, set it.</summary>
    public const string Prefix = "Link check: ";

    // Past any retry allowance, so the sync run never takes a link this check flagged for a failed write.
    private const int NoRetry = 1_000_000;

    // Media info is asked for in smaller batches, since each answer carries a file's streams and chapters.
    private const int DetailBatch = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly LinkMediaInfo? _mediaInfo;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkVerifier> _logger;

    public LinkVerifier(ILibraryManager libraryManager, LinkMediaInfo? mediaInfo, IServerApplicationHost applicationHost, ILogger<LinkVerifier> logger)
    {
        _libraryManager = libraryManager;
        _mediaInfo = mediaInfo;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Whether a row is errored because this check found its link could not play.</summary>
    public static bool Flagged(SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == SyncStatus.Errored && row.Reason?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Checks every settled link against its server. Links of a server that is not connected for this
    /// run, or that runs a Server Sync from before it answered this, are left as they are.
    /// </summary>
    /// <param name="table">The content sync table.</param>
    /// <param name="clientFor">The connected client for a server entry's key, or null when it is not connected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found.</returns>
    public async Task<LinkVerification> VerifyAsync(ContentSyncTableManager table, Func<string?, SourceServerClient?> clientFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(clientFor);
        int seen = 0, dead = 0, restored = 0, described = 0;
        var links = table.GetLinks().Where(row => row.StorageMode == StorageMode.Link && (row.Status == SyncStatus.Synced || Flagged(row)));
        foreach (var server in links.GroupBy(row => row.ServerKey ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (clientFor(server.Key) is not { } client)
            {
                continue;
            }

            var lacking = new List<SyncItem>();
            foreach (var batch in server.Chunk(PeerLinkDescribeRequest.MaxItems))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PeerLinkDescribeResponse? answer;
                string? refusal = null;
                try
                {
                    answer = await client.DescribeLinksAsync(Request(batch, details: false), cancellationToken).ConfigureAwait(false);
                    if (answer is null)
                    {
                        _logger.LogDebug("The server '{Server}' links stream from does not check links yet, so its links were not checked", server.Key);
                        break;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // The source answered and refused every link at once, such as with linking switched off.
                    answer = new PeerLinkDescribeResponse();
                    refusal = JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(ex.Message, 300);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // A source that is down is not a dead link: play falls back, and the next run checks again.
                    _logger.LogDebug("Could not check links against '{Server}': {Reason}", server.Key, ex.Message);
                    break;
                }

                var byId = answer.Items.GroupBy(i => i.ItemId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var row in batch)
                {
                    seen++;
                    byId.TryGetValue(row.SourceItemId, out var found);
                    if (refusal is null && found is { Exists: true })
                    {
                        var changed = false;
                        if (Flagged(row))
                        {
                            row.Status = SyncStatus.Synced;
                            row.Reason = null;
                            row.RetryCount = 0;
                            row.StatusDate = DateTime.UtcNow;
                            restored++;
                            changed = true;
                        }

                        // The size is what tells another server's copy is the same file, so it is kept current.
                        if (found.Size > 0 && found.Size != row.SourceSize)
                        {
                            row.SourceSize = found.Size;
                            changed = true;
                        }

                        if (changed)
                        {
                            table.Upsert(row);
                        }

                        if (Lacks(row))
                        {
                            lacking.Add(row);
                        }

                        continue;
                    }

                    var reason = Prefix + (refusal ?? found?.Reason ?? "the file is no longer on the source");
                    if (!Flagged(row) || !string.Equals(row.Reason, reason, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("The link {Path} cannot play: {Reason}", row.LocalPath, reason[Prefix.Length..]);
                        row.Status = SyncStatus.Errored;
                        row.Reason = reason;
                        row.RetryCount = NoRetry;
                        row.StatusDate = DateTime.UtcNow;
                        table.Upsert(row);
                    }

                    dead++;
                }
            }

            described += await DescribeAsync(client, lacking, cancellationToken).ConfigureAwait(false);
        }

        return new LinkVerification(seen, dead, restored, described);
    }

    private PeerLinkDescribeRequest Request(IEnumerable<SyncItem> rows, bool details)
        => new() { ServerId = _applicationHost.SystemId, ItemIds = rows.Select(r => r.SourceItemId).ToList(), Details = details };

    // Whether a link's item is in the library and still without media info.
    private bool Lacks(SyncItem row)
        => _mediaInfo is not null
            && Guid.TryParse(row.LocalItemId, out var id)
            && _libraryManager.GetItemById(id) is { } item
            && LinkMediaInfo.Lacks(item);

    private async Task<int> DescribeAsync(SourceServerClient client, List<SyncItem> lacking, CancellationToken cancellationToken)
    {
        var described = 0;
        foreach (var batch in lacking.Chunk(DetailBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var answer = await client.DescribeLinksAsync(Request(batch, details: true), cancellationToken).ConfigureAwait(false);
                foreach (var description in answer?.Items ?? new List<PeerLinkDescription>())
                {
                    var row = batch.FirstOrDefault(r => string.Equals(r.SourceItemId, description.ItemId, StringComparison.OrdinalIgnoreCase));
                    if (row is not null && description.Exists
                        && Guid.TryParse(row.LocalItemId, out var id) && _libraryManager.GetItemById(id) is { } item
                        && await _mediaInfo!.ApplyAsync(item, description, client, cancellationToken).ConfigureAwait(false))
                    {
                        described++;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Could not take media info for links: {Reason}", ex.Message);
                break;
            }
        }

        return described;
    }
}
