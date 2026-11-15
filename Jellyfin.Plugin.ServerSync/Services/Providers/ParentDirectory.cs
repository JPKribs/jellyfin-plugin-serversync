using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// Lists the servers this one pulls from whose Server Sync can answer a metadata lookup. Each is asked
/// once and the answer is remembered for a while.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ParentDirectory
{
    /// <summary>How long a server that answered is remembered.</summary>
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(5);

    /// <summary>How long a server has to answer.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a server that did not qualify is remembered.</summary>
    public static readonly TimeSpan FailedRemembered = TimeSpan.FromSeconds(30);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<ParentDirectory> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, string Url, Task<bool> Runs)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentDirectory"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="clientFactory">Builds clients for other servers.</param>
    /// <param name="applicationHost">Names this server.</param>
    /// <param name="logger">Logger instance.</param>
    public ParentDirectory(IPluginConfigurationManager configManager, ISourceServerClientFactory clientFactory, IServerApplicationHost applicationHost, ILogger<ParentDirectory> logger)
    {
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Lists the servers this one pulls from that run a Server Sync able to answer.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The server entries.</returns>
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

    /// <summary>Finds the server entry an address belongs to.</summary>
    /// <param name="url">The address.</param>
    /// <returns>The entry with the longest matching server URL, or null.</returns>
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

    /// <summary>Gets the plugin version.</summary>
    public string Version => _configManager.PluginVersion;

    /// <summary>Forgets every answer, so each server is asked again.</summary>
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
                _logger.LogInformation("Server Sync will not ask '{Server}' for metadata: {Reason}", FormatUtilities.SanitizeForLog(server.DisplayName), FormatUtilities.SanitizeForLog(HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion), 300));
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
            _logger.LogInformation("Server Sync will not ask '{Server}' for metadata for now: it could not be reached ({Error})", FormatUtilities.SanitizeForLog(server.DisplayName), FormatUtilities.SanitizeForLog(ex.Message, 300));
            return false;
        }
    }
}
