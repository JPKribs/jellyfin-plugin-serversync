using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// One unit of refresh work for Users, a single mapping and category pairing that carries the data
/// fetched from the source for one source user, so the network calls stay at one per source user.
/// </summary>
public sealed class UserCategoryWork
{
    /// <summary>Gets the server the settings are read from.</summary>
    public required ScanSource Source { get; init; }

    /// <summary>
    /// Gets a value indicating whether another server speaks for this user this run, so the row is
    /// only brought up to date with what this server holds and nothing is applied from it.
    /// </summary>
    public bool Observe { get; init; }

    /// <summary>Gets the mapping between the source user and the local user.</summary>
    public required UserMapping Mapping { get; init; }

    /// <summary>Gets the category of settings, such as Policy or Configuration.</summary>
    public required string Category { get; init; }

    /// <summary>Gets the source user as the source reports it.</summary>
    public required Jellyfin.Sdk.Generated.Models.UserDto SourceUser { get; init; }

    /// <summary>Gets the local user's settings.</summary>
    public required MediaBrowser.Model.Dto.UserDto LocalUserDto { get; init; }

    /// <summary>Gets the local user entity.</summary>
    public required Jellyfin.Database.Implementations.Entities.User LocalUser { get; init; }
}
