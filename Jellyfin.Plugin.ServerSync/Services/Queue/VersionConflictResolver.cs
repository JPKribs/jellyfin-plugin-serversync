using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Settles a value conflict between this server and a peer on origin versions, for the kinds that use
/// last write wins. Only recorded edits count. Jellyfin's saved dates are never used, because a
/// library scan moves them too, and a freshly scanned copy would otherwise outrank the edit it came
/// from. A recorded edit on either side beats an unrecorded value on the other, and when neither side
/// has recorded an edit the source wins, which is today's rule for scan only sources.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class VersionConflictResolver
{
    private static readonly TimeSpan CapabilityCache = TimeSpan.FromMinutes(5);
    private readonly VersionStore _versions;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<VersionConflictResolver> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime CheckedAt, bool Carries)> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionConflictResolver"/> class.
    /// </summary>
    /// <param name="versions">The version store.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public VersionConflictResolver(VersionStore versions, IServerApplicationHost applicationHost, ILogger<VersionConflictResolver> logger)
    {
        _versions = versions;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>The recorded version of this server's value, or null when no edit was ever recorded for it.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>The version, or null.</returns>
    public ObjectVersion? Recorded(HintKind kind, string localKey) => _versions.Get(kind, localKey);

    /// <summary>Records the version of a value this server now holds.</summary>
    /// <param name="version">The version.</param>
    public void Record(ObjectVersion version) => _versions.Set(version);

    /// <summary>A version for an edit made here, now.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>The version.</returns>
    public ObjectVersion LocalEditNow(HintKind kind, string localKey) => new()
    {
        Kind = kind,
        Key = localKey,
        ServerId = _applicationHost.SystemId,
        Timestamp = DateTime.UtcNow
    };

    /// <summary>Whether a connected peer answers version questions.</summary>
    /// <param name="source">The connected peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when it runs a Server Sync that carries versions.</returns>
    public async Task<bool> PeerCarriesVersionsAsync(ScanSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_capabilities.TryGetValue(source.Key, out var cached) && DateTime.UtcNow - cached.CheckedAt < CapabilityCache)
        {
            return cached.Carries;
        }

        bool carries;
        try
        {
            var capabilities = await source.Client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            carries = capabilities is not null && capabilities.Features.Contains(HintProtocol.HintFeature);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read capabilities from '{Peer}'", source.Name);
            carries = false;
        }

        _capabilities[source.Key] = (DateTime.UtcNow, carries);
        return carries;
    }

    /// <summary>
    /// Reads the versions a peer holds for a batch of its own keys, in requests of the size the peer
    /// accepts. Keys the peer never versioned are missing from the result.
    /// </summary>
    /// <param name="source">The connected peer.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="peerKeys">The peer's keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The versions by peer key, or null when the peer did not answer.</returns>
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read versions from '{Peer}'", source.Name);
                return null;
            }

            if (page is null)
            {
                return null;
            }

            foreach (var pair in page)
            {
                all[pair.Key] = pair.Value;
            }
        }

        return all;
    }

    /// <summary>Decides between this server's value and a peer's on their recorded versions. Values are known to differ.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <param name="peerVersion">The version the peer's value carries, or null when the peer never recorded an edit for it.</param>
    /// <returns>Apply when the peer's value wins, Keep when this server's does.</returns>
    public VersionDecision Decide(HintKind kind, string localKey, ObjectVersion? peerVersion)
    {
        var local = _versions.Get(kind, localKey);
        if (peerVersion is null)
        {
            return local is null ? VersionDecision.Apply : VersionDecision.Keep;
        }

        return local is null ? VersionDecision.Apply : VersionDecider.Decide(local, peerVersion, valuesEqual: false);
    }
}
