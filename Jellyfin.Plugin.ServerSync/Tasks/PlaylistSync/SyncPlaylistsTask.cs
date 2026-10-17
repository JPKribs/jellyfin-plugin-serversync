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

public class SyncPlaylistsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<SyncPlaylistsTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly PlaylistMirror _mirror;
    private readonly VersionConflictResolver _resolver;

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

    public string Name => "Sync Playlists";

    public string Key => "ServerSyncPlaylists";

    public string Description => "Mirrors the playlists of mapped users, holding the local copies of their items.";

    public string Category => "Server Sync";

    public bool IsHidden => true;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

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

        var local = _mirror.LocalPlaylists();
        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectVersion? peerVersion = null;
            if (twoWay && _mirror.FindByRoot(playlist.RootId, playlist.IsOriginal, local) is not null)
            {
                var localKey = HintProtocol.PlaylistKey(Guid.Parse(playlist.RootId));
                if (carries != true || peerVersions is null || !peerVersions.TryGetValue(playlist.RootId, out peerVersion))
                {
                    continue;
                }

                if (_resolver.Decide(HintKind.Playlists, localKey, peerVersion) == VersionDecision.Keep)
                {
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
            PruneDeleted(server, playlists, users);
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
