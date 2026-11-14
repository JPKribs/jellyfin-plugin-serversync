using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Settles a link whose own server no longer has the file. A film is there for as long as any server
/// this one reads from holds a real copy, so the other servers are asked first: the link moves to the
/// first that holds the file, in priority order, and goes only when none does.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkRehomer
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly ContentSyncTableManager _table;
    private readonly IServerApplicationHost _applicationHost;
    private readonly LinkWriter? _writer;
    private readonly ILogger<LinkRehomer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkRehomer"/> class.
    /// </summary>
    public LinkRehomer(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        ContentSyncTableManager table,
        IServerApplicationHost applicationHost,
        ILogger<LinkRehomer> logger,
        LinkWriter? writer = null)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _table = table;
        _applicationHost = applicationHost;
        _logger = logger;
        _writer = writer;
    }

    /// <summary>
    /// Moves the link to another server that holds its file, or marks it for removal when none does.
    /// </summary>
    /// <param name="row">The link's row. Its own server has said the file is gone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name of the server the link streams from now, or null when it was marked for removal.</returns>
    public async Task<string?> SettleGoneAsync(SyncItem row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var config = _configManager.Configuration;
        var copied = LinkPaths.CopiedPathOf(row);
        if (!string.IsNullOrEmpty(row.LocalPath) && !string.IsNullOrEmpty(copied))
        {
            var link = new LinkEntry(row.LocalPath, copied, row.SourceItemId, row.ServerKey, row.SourceSize);
            foreach (var alternate in LinkFallback.Alternates(config, link))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LinkStreamCheckResponse answer;
                try
                {
                    using var client = _clientFactory.Create(alternate.Server);
                    answer = await client.CheckLinkStreamAsync(
                        new LinkStreamCheckRequest { ServerId = _applicationHost.SystemId, Path = alternate.SourcePath, Probe = true },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogDebug(ex, "Could not ask '{Server}' whether it holds {Path}", alternate.Server.DisplayName, alternate.SourcePath);
                    continue;
                }

                if (!answer.Allowed || !Guid.TryParse(answer.ItemId, out var heldId))
                {
                    continue;
                }

                var mapping = alternate.Server.GetEnabledLibraryMappings()
                    .FirstOrDefault(m => LinkFallback.SourcePathFor(copied, m.LocalRootPath, m.SourceRootPath) is not null);
                var newId = heldId.ToString("N", CultureInfo.InvariantCulture);
                if (!string.Equals(newId, row.SourceItemId, StringComparison.OrdinalIgnoreCase))
                {
                    _table.Delete(row.SourceItemId);
                    row.SourceItemId = newId;
                }

                row.ServerKey = alternate.Server.Key;
                row.SourceLibraryId = mapping?.SourceLibraryId ?? row.SourceLibraryId;
                row.SourcePath = alternate.SourcePath;
                row.SourceSize = answer.Size > 0 ? answer.Size : row.SourceSize;
                row.StorageMode = StorageMode.Link;
                row.Status = SyncStatus.Queued;
                row.PendingType = null;
                row.Reason = null;
                row.RetryCount = 0;
                row.StatusDate = DateTime.UtcNow;
                _table.Upsert(row);

                // The .strm names the link by its id, so it is written now when the id changed, rather
                // than leave it naming a link nobody holds until the next run.
                try
                {
                    _writer?.Write(row, _table, config);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "The link {Path} is written again on the next run", row.LocalPath);
                }

                _logger.LogInformation("The link {Path} now streams from '{Server}', since the server it streamed from no longer has the file", row.LocalPath, alternate.Server.DisplayName);
                return alternate.Server.DisplayName;
            }
        }

        SyncStateService.ProcessMissingItem(_table, row, ApprovalMode.Enabled, _logger, ownLink: true);
        return null;
    }
}
