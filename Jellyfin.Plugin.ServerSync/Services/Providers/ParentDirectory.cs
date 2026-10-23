using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

[PluginService(ServiceLifetime.Singleton)]
public sealed class ParentDirectory
{
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan FailedRemembered = TimeSpan.FromSeconds(30);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<ParentDirectory> _logger;
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, string Url, Task<bool> Runs)> _known = new(StringComparer.OrdinalIgnoreCase);

    public ParentDirectory(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<ParentDirectory> logger)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SourceServer>> ParentsAsync(CancellationToken cancellationToken)
    {
        var servers = _configManager.Configuration.GetPullServers();
        if (servers.Count == 0)
        {
            return Array.Empty<SourceServer>();
        }

        var runs = await Task.WhenAll(servers.Select(RunsServerSync)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return servers.Where((_, i) => runs[i]).ToList();
    }

    public SourceServer? FindByUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return _configManager.Configuration.GetPullServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Url) && url.StartsWith(s.Url.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Url.Length)
            .FirstOrDefault();
    }

    public string Version => _configManager.PluginVersion;

    public void Forget()
    {
        lock (_gate)
        {
            _known.Clear();
        }
    }

    private Task<bool> RunsServerSync(SourceServer server)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_known.TryGetValue(server.Key, out var known)
                && string.Equals(known.Url, server.Url, StringComparison.OrdinalIgnoreCase)
                && now - known.At < (_failed.ContainsKey(server.Key) ? FailedRemembered : Remembered))
            {
                return known.Runs;
            }

            _failed.TryRemove(server.Key, out _);
            var probe = ProbeAsync(server);
            _known[server.Key] = (now, server.Url, probe);
            return probe;
        }
    }

    private async Task<bool> ProbeAsync(SourceServer server)
    {
        var runs = await AskAsync(server).ConfigureAwait(false);
        if (!runs)
        {
            _failed[server.Key] = 0;
        }

        return runs;
    }

    private async Task<bool> AskAsync(SourceServer server)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ProbeTimeout);
            using var client = _clientFactory.Create(server);
            var capabilities = await client.GetPeerCapabilitiesAsync(deadline.Token).ConfigureAwait(false);
            if (capabilities is null)
            {
                _logger.LogInformation("Server Sync will not ask '{Server}' for metadata: Server Sync is not installed there", server.DisplayName);
                return false;
            }

            if (!HintProtocol.Compatible(capabilities, _configManager.PluginVersion))
            {
                _logger.LogInformation("Server Sync will not ask '{Server}' for metadata: {Reason}", server.DisplayName, HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion));
                return false;
            }

            if (string.Equals(capabilities.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Server Sync will not ask '{Server}' for metadata: it reports this server's own id", server.DisplayName);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Server Sync will not ask '{Server}' for metadata for now: it could not be reached ({Error})", server.DisplayName, ex.Message);
            return false;
        }
    }
}
