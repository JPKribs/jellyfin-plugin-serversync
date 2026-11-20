using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The Link feature must be offered to peers only while a server entry here allows linking.
/// </summary>
public sealed class LinkFeatureTests
{
    /// <summary>
    /// The feature goes out under the name peers look for.
    /// True: a peer reading this server's capabilities finds the feature.
    /// False: a renamed feature would switch linking off across the pool.
    /// </summary>
    [Fact]
    public void Name_IsLink()
    {
        var name = LinkFeature.Name;

        Assert.Equal("Link", name);
    }

    /// <summary>
    /// A missing configuration or one with no server entries offers nothing.
    /// True: a server that is not set up never claims to stream links.
    /// False: reading the capabilities before setup would throw.
    /// </summary>
    /// <param name="hasConfig">Whether a configuration exists at all.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offered_NoServers_IsFalse(bool hasConfig)
    {
        var config = hasConfig ? new PluginConfiguration() : null;

        var offered = LinkFeature.Offered(config!);

        Assert.False(offered);
    }

    /// <summary>
    /// The feature is offered when one entry is enabled, allows linking, and pushes, and by no other entry.
    /// True: the feature follows the Allow linking setting of the servers this one sends to.
    /// False: a Pull, disabled, or unticked entry would advertise streams it then refuses.
    /// </summary>
    /// <param name="mode">The entry's direction.</param>
    /// <param name="enabled">Whether the entry is enabled.</param>
    /// <param name="allowLinking">Whether Allow linking is on.</param>
    /// <param name="expected">Whether the feature is offered.</param>
    [Theory]
    [InlineData(ServerMode.Push, true, true, true)]
    [InlineData(ServerMode.Sync, true, true, true)]
    [InlineData(ServerMode.Pull, true, true, false)]
    [InlineData(ServerMode.Push, false, true, false)]
    [InlineData(ServerMode.Push, true, false, false)]
    public void Offered_FollowsTheEntry(ServerMode mode, bool enabled, bool allowLinking, bool expected)
    {
        var config = new PluginConfiguration();
        config.Servers.Add(new SourceServer { Mode = ServerMode.Pull });
        config.Servers.Add(new SourceServer { Mode = mode, IsEnabled = enabled, AllowLinking = allowLinking });

        var offered = LinkFeature.Offered(config);

        Assert.Equal(expected, offered);
    }
}
