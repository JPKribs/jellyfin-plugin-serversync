using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Settles a value conflict between this server and a peer by their stamps, never by Jellyfin's saved dates, which a library scan moves.
/// A stamped edit beats an unstamped value, and the source wins when neither side has a stamp.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class VersionConflictResolver
{
    private static readonly TimeSpan CapabilityCache = TimeSpan.FromMinutes(5);
    private readonly VersionStore _versions;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<VersionConflictResolver> _logger;
    private readonly ConcurrentDictionary<string, (DateTime CheckedAt, bool? Carries)> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionConflictResolver"/> class.
    /// </summary>
    /// <param name="versions">Holds the stamps recorded on this server.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="configManager">Gives the plugin version a peer must be compatible with.</param>
    /// <param name="logger">Writes a peer whose stamps could not be read or trusted.</param>
    public VersionConflictResolver(VersionStore versions, IServerApplicationHost applicationHost, IPluginConfigurationManager configManager, ILogger<VersionConflictResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(logger);

        _versions = versions;
        _applicationHost = applicationHost;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>Reads the stamp of this server's value.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>The stamp, or null when no edit was ever recorded for it.</returns>
    public ObjectVersion? Recorded(HintKind kind, string localKey) => _versions.Get(kind, localKey);

    /// <summary>Records the stamp of a value this server now holds.</summary>
    /// <param name="version">The stamp to record, keyed by this server's key.</param>
    public void Record(ObjectVersion version) => _versions.Set(version);

    /// <summary>Builds a stamp for an edit made here at this moment.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>The stamp.</returns>
    public ObjectVersion LocalEditNow(HintKind kind, string localKey) => new()
    {
        Kind = kind,
        Key = localKey,
        ServerId = _applicationHost.SystemId,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>
    /// Asks whether a peer runs a Server Sync that keeps stamps, remembering only a real answer.
    /// A failed read is not taken as a no, since a scan would then let the source win over edits made here.
    /// </summary>
    /// <param name="source">The connected peer to ask.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>true</c> or <c>false</c> as the peer answered, or null when it could not be asked.</returns>
    public async Task<bool?> PeerCarriesVersionsAsync(ScanSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_capabilities.TryGetValue(source.Key, out var cached) && DateTime.UtcNow - cached.CheckedAt < CapabilityCache)
        {
            return cached.Carries;
        }

        bool? carries;
        try
        {
            var capabilities = await source.Client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities is null)
            {
                carries = false;
            }
            else if (HintProtocol.Compatible(capabilities, _configManager.PluginVersion))
            {
                carries = true;
            }
            else
            {
                carries = null;
                _logger.LogWarning("'{Peer}': {Reason}. Rows that would overwrite an edit recorded here wait until the two are compatible", FormatUtilities.SanitizeForLog(source.Name), FormatUtilities.SanitizeForLog(HintProtocol.ProtocolMismatch(capabilities, _configManager.PluginVersion), 300));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read capabilities from '{Peer}'", FormatUtilities.SanitizeForLog(source.Name));
            return null;
        }

        _capabilities[source.Key] = (DateTime.UtcNow, carries);
        return carries;
    }

    /// <summary>Whether a stamp is an edit this server made itself.</summary>
    /// <param name="version">The stamp to check.</param>
    /// <returns><c>true</c> when this server made it.</returns>
    public bool IsThisServer(ObjectVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Equals(version.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Keeps the stamp a peer held for a value the scan queued, until that value applies.</summary>
    /// <param name="version">The peer's stamp, keyed by this server's key.</param>
    public void RecordPending(ObjectVersion version) => _versions.SetPending(version);

    /// <summary>Forgets any pending stamp for an object.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    public void ClearPending(HintKind kind, string localKey) => _versions.ClearPending(kind, localKey);

    /// <summary>Records the pending stamp of a value that was applied.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns><c>true</c> when one was pending.</returns>
    public bool PromotePending(HintKind kind, string localKey) => _versions.PromotePending(kind, localKey);

    /// <summary>
    /// Reads the stamps a peer holds for a batch of its own keys, in requests of the size the peer accepts.
    /// </summary>
    /// <param name="source">The connected peer.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="peerKeys">The peer's keys for the objects.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The stamps by peer key, without keys the peer never stamped, or null when the peer did not answer.</returns>
    public async Task<Dictionary<string, ObjectVersion>?> PeerVersionsAsync(ScanSource source, HintKind kind, IReadOnlyList<string> peerKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(peerKeys);

        var all = new Dictionary<string, ObjectVersion>(StringComparer.Ordinal);
        for (var offset = 0; offset < peerKeys.Count; offset += HintProtocol.MaxHintsPerRequest)
        {
            var batch = peerKeys.Skip(offset).Take(HintProtocol.MaxHintsPerRequest).ToList();
            Dictionary<string, ObjectVersion>? page;
            try
            {
                page = await source.Client.GetPeerVersionsAsync(new VersionsRequest { Kind = kind, Keys = batch }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read versions from '{Peer}'", FormatUtilities.SanitizeForLog(source.Name));
                return null;
            }

            if (page is null)
            {
                return null;
            }

            var now = DateTime.UtcNow;
            foreach (var pair in page)
            {
                // A stamp far ahead of this clock is a broken clock or a lie.
                if (HintProtocol.IsFutureVersion(pair.Value.Timestamp, now))
                {
                    _logger.LogWarning("'{Peer}' holds a version of {Key} dated {At:u}, ahead of this server's clock. Ignoring it", FormatUtilities.SanitizeForLog(source.Name), FormatUtilities.SanitizeForLog(pair.Key), pair.Value.Timestamp);
                    continue;
                }

                pair.Value.Timestamp = HintProtocol.BoundVersion(pair.Value.Timestamp, now);
                all[pair.Key] = pair.Value;
            }
        }

        return all;
    }

    /// <summary>Decides between this server's value and a peer's by their stamps, for values known to differ.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <param name="peerVersion">The stamp the peer's value carries, or null when the peer never recorded an edit for it.</param>
    /// <returns>Apply when the peer's value wins, Keep when this server's does.</returns>
    public VersionDecision Decide(HintKind kind, string localKey, ObjectVersion? peerVersion)
    {
        var local = _versions.Get(kind, localKey);
        if (peerVersion is null)
        {
            return local is null ? VersionDecision.Apply : VersionDecision.Keep;
        }

        return VersionDecider.Decide(local, peerVersion, valuesEqual: false);
    }
}
