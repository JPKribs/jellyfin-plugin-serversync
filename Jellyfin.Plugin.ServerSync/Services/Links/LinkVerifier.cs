using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Checks this server's links against the servers they stream from as part of every content sync, so a
/// link that can no longer play shows on the Sync tab with the reason and one that can play again is
/// settled. Links whose item still has no media info are given the source's.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkVerifier
{
    /// <summary>How a row's reason starts when this check, rather than writing the link, set it.</summary>
    public const string Prefix = "Link check: ";

    /// <summary>A retry count past any allowance, so the sync run never takes a flagged link for a failed write.</summary>
    private const int NoRetry = 1_000_000;

    /// <summary>How many links media info is asked for at once, kept small since each answer carries streams and chapters.</summary>
    private const int DetailBatch = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly LinkMediaInfo? _mediaInfo;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LinkVerifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkVerifier"/> class.
    /// </summary>
    /// <param name="libraryManager">Finds the item each link stands for.</param>
    /// <param name="mediaInfo">Takes the source's media info, or null to take none.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="logger">Writes log entries.</param>
    public LinkVerifier(ILibraryManager libraryManager, LinkMediaInfo? mediaInfo, IServerApplicationHost applicationHost, ILogger<LinkVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(logger);
        _libraryManager = libraryManager;
        _mediaInfo = mediaInfo;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Whether a row is errored because this check found its link could not play.</summary>
    /// <param name="row">The row whose status and reason are read.</param>
    /// <returns><c>true</c> when the check flagged it.</returns>
    public static bool Flagged(SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == SyncStatus.Errored && row.Reason?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Checks every settled link against its server. Links of a server that is not connected for this
    /// run, or whose Server Sync does not answer the check, are left as they are.
    /// </summary>
    /// <param name="table">The content sync table, whose link rows are read and updated.</param>
    /// <param name="clientFor">The connected client for a server entry's key, or null when it is not connected.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
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
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    // The source refused every link at once, as with linking switched off.
                    answer = new PeerLinkDescribeResponse();
                    refusal = FormatUtilities.SanitizeForLog(ex.Message, 300);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // A source that is down is not a dead link, so nothing is flagged.
                    _logger.LogDebug(ex, "Could not check links against '{Server}'", server.Key);
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

                        var replaced = false;

                        // A changed size means the file was replaced and its media info is stale.
                        if (found.Size > 0 && found.Size != row.SourceSize)
                        {
                            replaced = row.SourceSize > 0;
                            row.SourceSize = found.Size;
                            changed = true;
                        }

                        if (changed)
                        {
                            table.Upsert(row);
                        }

                        if (replaced || Lacks(row))
                        {
                            lacking.Add(row);
                        }

                        continue;
                    }

                    var reason = Prefix + (refusal ?? (string.IsNullOrWhiteSpace(found?.Reason) ? "the file is no longer on the source" : FormatUtilities.SanitizeForLog(found!.Reason, 300)));
                    if (!Flagged(row) || !string.Equals(row.Reason, reason, StringComparison.Ordinal))
                    {
                        _logger.LogWarning("The link {Path} cannot play: {Reason}", FormatUtilities.SanitizeForLog(row.LocalPath), FormatUtilities.SanitizeForLog(reason[Prefix.Length..], 300));
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

    /// <summary>
    /// Whether a link's own server says the file is no longer there. Returns <c>false</c> when the server
    /// could not be asked, refused the question, or only failed to read the file, so an outage is never
    /// taken for a removal.
    /// </summary>
    /// <param name="row">The row of the link to ask about.</param>
    /// <param name="client">The server the link streams from.</param>
    /// <param name="cancellationToken">Cancels the asking.</param>
    /// <returns><c>true</c> when the server answered that it no longer has the file.</returns>
    public async Task<bool> GoneFromSourceAsync(SyncItem row, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var answer = await client.DescribeLinksAsync(Request(new[] { row }, details: false), cancellationToken).ConfigureAwait(false);
            return answer?.Items.FirstOrDefault(i => string.Equals(i.ItemId, row.SourceItemId, StringComparison.OrdinalIgnoreCase)) is { Exists: false } found
                && !string.Equals(found.Reason, PeerLinkDescription.Unreadable, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not ask whether {Path} is still on its source", FormatUtilities.SanitizeForLog(row.SourcePath));
            return false;
        }
    }

    /// <summary>
    /// Takes the source's media info again for a link that was already in the library, after the file
    /// behind it changed. Jellyfin never probes a .strm that already has streams, so without this a
    /// re-encoded file would keep showing, and be played by, the old file's codecs.
    /// </summary>
    /// <param name="row">The row of the link whose file changed.</param>
    /// <param name="client">The server the link streams from.</param>
    /// <param name="cancellationToken">Cancels the asking and the saving.</param>
    /// <returns><c>true</c> when the item now has the source's media info.</returns>
    public async Task<bool> RedescribeAsync(SyncItem row, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(client);
        if (_mediaInfo is null || string.IsNullOrEmpty(row.LocalPath) || _libraryManager.FindByPath(row.LocalPath, false) is not { } item)
        {
            return false;
        }

        try
        {
            var answer = await client.DescribeLinksAsync(Request(new[] { row }, details: true), cancellationToken).ConfigureAwait(false);
            return answer?.Items.FirstOrDefault(i => string.Equals(i.ItemId, row.SourceItemId, StringComparison.OrdinalIgnoreCase)) is { Exists: true } description
                && await _mediaInfo.ApplyAsync(item, description, client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not take the media info for {Path} again", FormatUtilities.SanitizeForLog(row.LocalPath));
            return false;
        }
    }

    /// <summary>Builds the request that asks a server about some links.</summary>
    private PeerLinkDescribeRequest Request(IEnumerable<SyncItem> rows, bool details)
        => new() { ServerId = _applicationHost.SystemId, ItemIds = rows.Select(r => r.SourceItemId).ToList(), Details = details };

    /// <summary>Whether a link's item is in the library and still without media info.</summary>
    private bool Lacks(SyncItem row)
        => _mediaInfo is not null
            && Guid.TryParse(row.LocalItemId, out var id)
            && _libraryManager.GetItemById(id) is { } item
            && LinkMediaInfo.Lacks(item);

    /// <summary>Gives the links that lack media info the source's, and counts those that took it.</summary>
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
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not take media info for links");
                break;
            }
        }

        return described;
    }
}
