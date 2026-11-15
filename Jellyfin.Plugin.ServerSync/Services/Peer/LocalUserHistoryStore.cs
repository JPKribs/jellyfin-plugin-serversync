using System;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Reads and writes user history in this server's library.
/// </summary>
[PluginService(ServiceLifetime.Transient, ServiceType = typeof(IUserHistoryStore))]
public sealed class LocalUserHistoryStore : IUserHistoryStore
{
    private readonly LocalServerClient _localClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalUserHistoryStore"/> class.
    /// </summary>
    /// <param name="localClient">Reads and writes this server's user data.</param>
    public LocalUserHistoryStore(LocalServerClient localClient)
    {
        ArgumentNullException.ThrowIfNull(localClient);
        _localClient = localClient;
    }

    /// <inheritdoc />
    public PeerHistoryState? Read(Guid userId, Guid itemId)
    {
        var data = _localClient.GetUserItemData(userId, itemId);
        return data is null ? null : PeerHistoryNegotiator.FromUserData(data);
    }

    /// <inheritdoc />
    public bool Write(Guid userId, Guid itemId, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _localClient.UpdateUserItemData(
            userId,
            itemId,
            state.Played,
            state.PlayCount,
            state.PlaybackPositionTicks,
            state.LastPlayedDate,
            state.IsFavorite,
            clearLastPlayedDate: state.Played == false);
    }
}
