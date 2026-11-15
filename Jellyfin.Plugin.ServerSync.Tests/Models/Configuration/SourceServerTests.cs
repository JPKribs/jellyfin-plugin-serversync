using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Configuration;

/// <summary>
/// A server entry's access level decides which users it maps and which modes it may keep.
/// </summary>
public class SourceServerTests
{
    private const string SelfId = "0f8fad5bd9cb469fa16570867728950e";
    private const string OtherId = "7c9e6679742540de944be07fc1f90ae7";

    private static SourceServer StandardUser(ServerMode mode = ServerMode.Pull) => new()
    {
        Name = "peer",
        Url = "http://peer:8096",
        ApiKey = "k",
        Mode = mode,
        AccessLevel = SourceServer.StandardUserAccess,
        AuthenticatedUser = "self",
        AuthenticatedUserId = SelfId,
        UserMappings = new List<UserMapping>
        {
            new() { IsEnabled = true, SourceUserId = "0f8fad5b-d9cb-469f-a165-70867728950e", LocalUserId = SelfId },
            new() { IsEnabled = true, SourceUserId = OtherId, LocalUserId = OtherId }
        }
    };

    /// <summary>
    /// A standard user's server maps only that user.
    /// True: the entry never reads or writes another local user's history through this server.
    /// False: a standard user's key could sync every mapped user.
    /// </summary>
    [Fact]
    public void StandardUser_OnlyMapsItsOwnUser()
    {
        var server = StandardUser();

        var mappings = server.GetEnabledUserMappings();

        Assert.Single(mappings);
        Assert.True(server.MaySyncUser(SelfId));
        Assert.False(server.MaySyncUser(OtherId));
    }

    /// <summary>
    /// An administrator's server maps every enabled user.
    /// True: an administrator key syncs the whole mapping list.
    /// False: administrators would be cut down to one user.
    /// </summary>
    [Fact]
    public void Administrator_MapsEveryUser()
    {
        var server = StandardUser();
        server.AccessLevel = SourceServer.AdministratorAccess;

        Assert.Equal(2, server.GetEnabledUserMappings().Count);
    }

    /// <summary>
    /// A standard user whose id is not known keeps every mapping.
    /// True: an entry saved before the id was recorded keeps working.
    /// False: older entries would silently lose every user mapping.
    /// </summary>
    [Fact]
    public void StandardUser_WithoutKnownId_KeepsEveryMapping()
    {
        var server = StandardUser();
        server.AuthenticatedUserId = string.Empty;

        Assert.Equal(2, server.GetEnabledUserMappings().Count);
    }

    /// <summary>
    /// A standard user may still push and sync, and a save keeps the mode.
    /// True: sending a user's history is allowed for a standard user.
    /// False: saving settings would quietly turn Sync or Push entries into Pull.
    /// </summary>
    [Fact]
    public void StandardUser_KeepsPushAndSync()
    {
        var server = StandardUser(ServerMode.Sync);
        Assert.True(server.Pushes);
        Assert.True(server.Pulls);

        var config = new PluginConfiguration { Servers = new List<SourceServer> { server } };
        config.SanitizeValues();

        Assert.Equal(ServerMode.Sync, config.Servers[0].Mode);
    }

    /// <summary>
    /// An entry of unknown access level keeps every mapping and its mode.
    /// True: an older entry is not narrowed by a save.
    /// False: saving would drop mappings or change the mode of an entry that predates access levels.
    /// </summary>
    [Fact]
    public void UnknownAccess_KeepsEveryMappingAndMode()
    {
        var server = StandardUser(ServerMode.Push);
        server.AccessLevel = string.Empty;

        var config = new PluginConfiguration { Servers = new List<SourceServer> { server } };
        config.SanitizeValues();

        Assert.Equal(ServerMode.Push, config.Servers[0].Mode);
        Assert.True(config.Servers[0].Pushes);
        Assert.Equal(2, config.Servers[0].GetEnabledUserMappings().Count);
    }
}
