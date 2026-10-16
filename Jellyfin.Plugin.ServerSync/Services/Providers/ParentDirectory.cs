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

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<ParentDirectory> _logger;
    private readonly object _gate = new();
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
            if (_known.TryGetValue(server.Key, out var known) && now - known.At < Remembered && string.Equals(known.Url, server.Url, StringComparison.OrdinalIgnoreCase))
            {
                return known.Runs;
            }

            var probe = ProbeAsync(server);
            _known[server.Key] = (now, server.Url, probe);
            return probe;
        }
    }

    private async Task<bool> ProbeAsync(SourceServer server)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ProbeTimeout);
            using var client = _clientFactory.Create(server);
            var capabilities = await client.GetPeerCapabilitiesAsync(deadline.Token).ConfigureAwait(false);
            return capabilities is not null
                && HintProtocol.SameVersion(capabilities.PluginVersion, _configManager.PluginVersion)
                && !string.Equals(capabilities.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not ask '{Server}' whether it runs Server Sync", server.DisplayName);
            return false;
        }
    }
}
