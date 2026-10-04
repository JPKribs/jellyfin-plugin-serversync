using System;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// <see cref="IUserHistoryStore"/> backed by this server's library through <see cref="LocalServerClient"/>.
/// </summary>
[PluginService(ServiceLifetime.Transient, ServiceType = typeof(IUserHistoryStore))]
public sealed class LocalUserHistoryStore : IUserHistoryStore
{
    private readonly LocalServerClient _localClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalUserHistoryStore"/> class.
    /// </summary>
    /// <param name="localClient">Client for this server's library and user data.</param>
    public LocalUserHistoryStore(LocalServerClient localClient)
    {
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
