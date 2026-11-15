using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Copies the playlists of mapped users between this server and the servers it syncs playlists with, as the last step of Sync Information.
/// </summary>
public class SyncPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// The playlists a higher ranked server already spoke for this run, when rank decides.
    /// </summary>
    private readonly HashSet<string> _spokenFor = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<SyncPlaylistsTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly PlaylistMirror _mirror;
    private readonly VersionConflictResolver _resolver;

    /// <summary>Initializes a new instance of the <see cref="SyncPlaylistsTask"/> class.</summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source server.</param>
    /// <param name="mirror">The service that makes a local playlist match another server's.</param>
    /// <param name="resolver">The resolver that decides which of two edits wins.</param>
    public SyncPlaylistsTask(
        ILogger<SyncPlaylistsTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        PlaylistMirror mirror,
        VersionConflictResolver resolver)
    {
        _logger = logger;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _mirror = mirror;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public string Name => "Sync Playlists";

    /// <inheritdoc />
    public string Key => "ServerSyncPlaylists";

    /// <inheritdoc />
    public string Description => "Mirrors the playlists of mapped users, holding the local copies of their items.";

    /// <inheritdoc />
    public string Category => "Server Sync";

    /// <inheritdoc />
    public bool IsHidden => true;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = _configManager.Configuration;
        if (!config.EnablePlaylistSync)
        {
            return;
        }

        var servers = config.GetPullServers().Where(s => s.GetEnabledUserMappings().Count > 0).ToList();
        if (servers.Count == 0)
        {
            _logger.LogInformation("{Task}: no server has a user mapping, nothing to mirror", Name);
            progress.Report(100);
            return;
        }

        var mutex = SyncModuleMutex.ForModule("Playlists");
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _mirror.RebuildCopiesFromTags();
            _spokenFor.Clear();
            var failures = 0;
            var waiting = 0;
            for (var i = 0; i < servers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var server = servers[i];
                try
                {
                    await MirrorServerAsync(server, i, config.PlaylistSyncShares, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (System.Net.Http.HttpRequestException ex) when ((int?)ex.StatusCode == 426)
                {
                    waiting++;
                    _logger.LogWarning("{Task}: '{Server}' runs another Server Sync version, so its playlists wait until both match", Name, server.DisplayName);
                }
                catch (Exception ex)
                {
                    failures++;
                    _logger.LogWarning(ex, "{Task}: could not read the playlists of '{Server}'. Its local copies are left as they are", Name, server.DisplayName);
                }

                progress.Report(100.0 * (i + 1) / servers.Count);
            }

            if (failures > 0 && failures + waiting == servers.Count)
            {
                throw new InvalidOperationException("no server's playlists could be read");
            }
        }
        finally
        {
            mutex.Release();
        }
    }

    private async Task MirrorServerAsync(SourceServer server, int priority, bool syncShares, CancellationToken cancellationToken)
    {
        using var source = new ScanSource(server, _clientFactory.Create(server), priority);
        var connection = await source.Client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connection.Success)
        {
            throw new InvalidOperationException(connection.ErrorMessage ?? connection.Message ?? "the server is unreachable");
        }

        var users = _mirror.UserMap(server);
        if (users.Count == 0)
        {
            return;
        }

        var playlists = await _mirror.ReadAsync(server, source.Client, users, null, cancellationToken).ConfigureAwait(false);

        var twoWay = server.Mode == ServerMode.Sync;
        bool? carries = null;
        Dictionary<string, ObjectVersion>? peerVersions = null;
        if (twoWay)
        {
            carries = await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false);
            if (carries == true)
            {
                peerVersions = await _resolver.PeerVersionsAsync(source, HintKind.Playlists, playlists.Select(p => p.RootId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);
            }
        }

        var config = _configManager.Configuration;
        var byRank = config.PlaylistSyncPrecedence == SyncPrecedence.Rank;
        var local = _mirror.LocalPlaylists();
        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // By rank, the highest ranked server that holds a playlist is the only one heard for it.
            if (byRank && !_spokenFor.Add(playlist.RootId))
            {
                continue;
            }

            ObjectVersion? peerVersion = null;
            if (twoWay)
            {
                var localKey = HintProtocol.PlaylistKey(Guid.Parse(playlist.RootId));
                var known = peerVersions is not null && peerVersions.TryGetValue(playlist.RootId, out peerVersion);
                if (_mirror.FindByRoot(playlist.RootId, playlist.IsOriginal, local) is not null)
                {
                    if (carries != true || !known)
                    {
                        peerVersion = null;
                        continue;
                    }

                    if (_resolver.Decide(HintKind.Playlists, localKey, peerVersion) == VersionDecision.Keep)
                    {
                        continue;
                    }
                }
                else if (_resolver.Recorded(HintKind.Playlists, localKey) is { } gone
                    && (peerVersion is null || VersionDecider.SameVersion(peerVersion, gone) || !VersionDecider.IsNewer(peerVersion, gone)))
                {
                    // This server knew the playlist and no longer holds it, so it was deleted here or
                    // by a server this one syncs with. It comes back only when the other server's copy
                    // was edited after that.
                    continue;
                }
            }

            try
            {
                var outcome = await _mirror.ApplyAsync(server, playlist, users, syncShares, cancellationToken, local).ConfigureAwait(false);
                if (outcome == PlaylistMirrorOutcome.Created)
                {
                    local = _mirror.LocalPlaylists();
                }

                if (peerVersion is not null && outcome is PlaylistMirrorOutcome.Created or PlaylistMirrorOutcome.Updated)
                {
                    _resolver.Record(peerVersion);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Task}: could not mirror the playlist '{Name}' from '{Server}'", Name, playlist.Name, server.DisplayName);
            }
        }

        if (!twoWay)
        {
            if (config.PlaylistRemovedAction == PlaylistRemovedAction.Delete)
            {
                PruneDeleted(server, playlists, users);
            }

            return;
        }

        if (carries == true)
        {
            await SettleDeletedAsync(source, playlists, users, config.PlaylistRemovedAction, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A copy this server took from a Sync server that the server no longer lists was deleted there,
    /// when that server also holds a newer version of it than this one does: the deletion. A deletion
    /// announced as it happened is settled the same way, so this only catches the ones that were missed.
    /// The copy is deleted or kept by Removed Playlists, and the deletion is recorded either way, so
    /// the playlist is not handed back to the server that deleted it. A copy edited here since is kept
    /// and stays this server's newer edit, which that server takes back on its own scan.
    /// </summary>
    private async Task SettleDeletedAsync(ScanSource source, List<SourcePlaylist> listed, Dictionary<string, Guid> users, PlaylistRemovedAction action, CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(listed.Select(p => p.RootId), StringComparer.OrdinalIgnoreCase);
        var owners = new HashSet<Guid>(users.Values);
        var missing = _mirror.LocalPlaylists()
            .Where(p => string.Equals(PlaylistMirror.CopyServer(p), source.Key, StringComparison.OrdinalIgnoreCase)
                && owners.Contains(p.OwnerUserId)
                && !roots.Contains(PlaylistMirror.RootKey(p)))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var versions = await _resolver.PeerVersionsAsync(source, HintKind.Playlists, missing.Select(PlaylistMirror.RootKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);
        if (versions is null)
        {
            return;
        }

        foreach (var playlist in missing)
        {
            var root = PlaylistMirror.RootKey(playlist);
            if (!Guid.TryParse(root, out var rootId) || !versions.TryGetValue(root, out var theirs))
            {
                continue;
            }

            var localKey = HintProtocol.PlaylistKey(rootId);
            if (_resolver.Recorded(HintKind.Playlists, localKey) is { } mine
                && (VersionDecider.SameVersion(theirs, mine) || !VersionDecider.IsNewer(theirs, mine)))
            {
                continue;
            }

            theirs.Kind = HintKind.Playlists;
            theirs.Key = localKey;
            if (action == PlaylistRemovedAction.Delete)
            {
                _logger.LogInformation("{Task}: '{Server}' deleted the playlist '{Name}', so the copy here is deleted", Name, source.Name, playlist.Name);
                _mirror.Delete(playlist);
            }
            else
            {
                _logger.LogInformation("{Task}: '{Server}' deleted the playlist '{Name}'. The copy here is kept, as Removed Playlists is set to Keep", Name, source.Name, playlist.Name);
            }

            _resolver.Record(theirs);
        }
    }

    private void PruneDeleted(SourceServer server, List<SourcePlaylist> playlists, Dictionary<string, Guid> users)
    {
        var roots = new HashSet<string>(playlists.Select(p => p.RootId), StringComparer.OrdinalIgnoreCase);
        var owners = new HashSet<Guid>(users.Values);
        var gone = _mirror.LocalPlaylists()
            .Where(p => string.Equals(PlaylistMirror.CopyServer(p), server.Key, StringComparison.OrdinalIgnoreCase)
                && owners.Contains(p.OwnerUserId)
                && !roots.Contains(PlaylistMirror.RootKey(p)))
            .ToList();
        if (gone.Count == 0)
        {
            return;
        }

        if (playlists.Count == 0)
        {
            _logger.LogWarning("{Task}: '{Server}' listed no playlists for its mapped users, so {Count} local copies are kept this run", Name, server.DisplayName, gone.Count);
            return;
        }

        foreach (var playlist in gone)
        {
            _mirror.Delete(playlist);
        }
    }
}
