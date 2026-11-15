using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

/// <summary>
/// The server entry readers return only the entries a Direction, a key, or an ID names, and never null.
/// </summary>
public sealed class PluginConfigurationExtensionsTests
{
    private const string PeerId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";

    /// <summary>
    /// Each Direction puts an entry on the pull list, the push list, or both.
    /// True: Pull is only read from, Push is only sent to, and Sync is both.
    /// False: a Push server would be scanned, or a Pull server would be sent hints.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Pull, true, false)]
    [InlineData(ServerMode.Push, false, true)]
    [InlineData(ServerMode.Sync, true, true)]
    public void GetPullAndPushServers_ByDirection_ListTheEntryOnTheRightSide(ServerMode mode, bool pulls, bool pushes)
    {
        var config = With(Server("a", mode));

        var pullCount = config.GetPullServers().Count;
        var pushCount = config.GetPushServers().Count;

        Assert.Equal(pulls ? 1 : 0, pullCount);
        Assert.Equal(pushes ? 1 : 0, pushCount);
    }

    /// <summary>
    /// A disabled entry, or one missing its URL or API Key, takes part in nothing.
    /// True: an entry that is off or half set up is never called.
    /// False: sync would call an entry the operator switched off or never finished.
    /// </summary>
    [Theory]
    [InlineData(false, "http://a:8096", "key")]
    [InlineData(true, "", "key")]
    [InlineData(true, "http://a:8096", " ")]
    public void GetActiveServers_DisabledOrUnconfigured_IsLeftOut(bool enabled, string url, string apiKey)
    {
        var server = Server("a", ServerMode.Sync);
        server.IsEnabled = enabled;
        server.Url = url;
        server.ApiKey = apiKey;
        var config = With(server);

        var active = config.GetActiveServers();

        Assert.Empty(active);
        Assert.Empty(config.GetPullServers());
        Assert.Empty(config.GetPushServers());
    }

    /// <summary>
    /// The active list holds every Direction once, in list order.
    /// True: a Sync entry is not counted twice and the order is the operator's.
    /// False: a Sync server would be processed twice, or out of Rank order.
    /// </summary>
    [Fact]
    public void GetActiveServers_MixedDirections_ListsEachOnceInOrder()
    {
        var config = With(Server("push", ServerMode.Push), Server("sync", ServerMode.Sync), Server("pull", ServerMode.Pull));

        var keys = config.GetActiveServers().Select(s => s.Key).ToArray();

        Assert.Equal(new[] { "push", "sync", "pull" }, keys);
    }

    /// <summary>
    /// A configuration whose server list is null reads as having no servers.
    /// True: every list reader returns an empty list and every finder returns null.
    /// False: a settings file without a server list would throw on every run.
    /// </summary>
    [Fact]
    public void Readers_NullServerList_ReturnEmptyAndNull()
    {
        var config = new PluginConfiguration { Servers = null! };

        Assert.Empty(config.GetPullServers());
        Assert.Empty(config.GetPushServers());
        Assert.Empty(config.GetActiveServers());
        Assert.Empty(config.GetEnabledLibraryMappings());
        Assert.Empty(config.GetEnabledUserMappings());
        Assert.Empty(config.GetAllLibraryMappings());
        Assert.Null(config.FindServer("a"));
        Assert.Null(config.FindServerById(PeerId));
        Assert.Null(config.ResolveServer(null));
        Assert.False(config.NegotiatesAnyHistory());
        Assert.False(config.NegotiatesHistory("a", "lib"));
    }

    /// <summary>
    /// A null configuration is refused by every reader that guards its argument.
    /// True: the caller's mistake is named at once.
    /// False: a null would surface later as an unrelated null reference.
    /// </summary>
    [Fact]
    public void Readers_NullConfiguration_Throw()
    {
        PluginConfiguration config = null!;

        Assert.Throws<ArgumentNullException>(() => config.GetPullServers());
        Assert.Throws<ArgumentNullException>(() => config.GetPushServers());
        Assert.Throws<ArgumentNullException>(() => config.GetActiveServers());
        Assert.Throws<ArgumentNullException>(() => config.NegotiatesAnyHistory());
        Assert.Throws<ArgumentNullException>(() => config.FindServer("a"));
        Assert.Throws<ArgumentNullException>(() => config.FindServerById("a"));
        Assert.Throws<ArgumentNullException>(() => config.GetEnabledLibraryMappings());
        Assert.Throws<ArgumentNullException>(() => config.GetEnabledUserMappings());
        Assert.Throws<ArgumentNullException>(() => config.GetAllLibraryMappings());
    }

    /// <summary>
    /// An entry is found by its key whatever the case, and a missing key finds nothing.
    /// True: a row's key reaches its entry, and no key reaches none.
    /// False: a row would lose its server over letter case, or an empty key would match an entry.
    /// </summary>
    [Theory]
    [InlineData("abc", true)]
    [InlineData("ABC", true)]
    [InlineData("other", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FindServer_ByKey_MatchesWithoutCase(string? key, bool found)
    {
        var config = With(Server("abc", ServerMode.Pull));

        var server = config.FindServer(key);

        Assert.Equal(found, server is not null);
    }

    /// <summary>
    /// An entry is found by the server's own ID whatever the case, even when the entry is disabled.
    /// True: a peer that names itself reaches its entry, so a disabled one can be refused by name.
    /// False: a peer would be told it is not listed when it is only switched off.
    /// </summary>
    [Theory]
    [InlineData(PeerId, true)]
    [InlineData("0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F", true)]
    [InlineData("ffffffffffffffffffffffffffffffff", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FindServerById_ByServerId_MatchesWithoutCase(string? serverId, bool found)
    {
        var entry = Server("abc", ServerMode.Pull);
        entry.IsEnabled = false;
        var config = With(entry);

        var server = config.FindServerById(serverId);

        Assert.Equal(found, server is not null);
    }

    /// <summary>
    /// An entry with an empty server ID is never matched by an empty ID.
    /// True: a caller that names no server reaches no entry.
    /// False: an entry never tested would stand in for any caller that sends no ID.
    /// </summary>
    [Fact]
    public void FindServerById_EntryWithoutAnId_IsNotMatchedByAnEmptyId()
    {
        var entry = Server("abc", ServerMode.Pull);
        entry.ServerId = string.Empty;
        var config = With(entry);

        var server = config.FindServerById(string.Empty);

        Assert.Null(server);
    }

    /// <summary>
    /// A row whose key names no entry falls back to the first source, and to nothing when there is no source.
    /// True: an old row without a key still resolves, and a Push only setup resolves none.
    /// False: old rows would lose their server, or a Push server would be read as a source.
    /// </summary>
    [Fact]
    public void ResolveServer_UnknownKey_FallsBackToTheFirstSourceOnly()
    {
        var push = Server("push", ServerMode.Push);
        var pull = Server("pull", ServerMode.Pull);

        var withSource = With(push, pull).ResolveServer("gone");
        var withoutSource = With(push).ResolveServer("gone");

        Assert.Same(pull, withSource);
        Assert.Null(withoutSource);
    }

    /// <summary>
    /// History is negotiated only for an enabled Sync entry whose enabled mapping of that library chose to.
    /// True: plays are written back only where the operator asked for it.
    /// False: a Pull server, a disabled entry, or another library would have history written to it.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Sync, true, true, true, "LIB", true)]
    [InlineData(ServerMode.Pull, true, true, true, "lib", false)]
    [InlineData(ServerMode.Push, true, true, true, "lib", false)]
    [InlineData(ServerMode.Sync, false, true, true, "lib", false)]
    [InlineData(ServerMode.Sync, true, false, true, "lib", false)]
    [InlineData(ServerMode.Sync, true, true, false, "lib", false)]
    [InlineData(ServerMode.Sync, true, true, true, "other", false)]
    [InlineData(ServerMode.Sync, true, true, true, null, false)]
    public void NegotiatesHistory_ByEntryAndMapping_IsTrueOnlyForAnOptedInSyncLibrary(ServerMode mode, bool serverEnabled, bool mappingEnabled, bool negotiate, string? libraryId, bool expected)
    {
        var server = Server("a", mode);
        server.IsEnabled = serverEnabled;
        server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib", IsEnabled = mappingEnabled, NegotiateHistory = negotiate });
        var config = With(server);

        var negotiates = config.NegotiatesHistory("a", libraryId);

        Assert.Equal(expected, negotiates);
        Assert.Equal(mode == ServerMode.Sync && serverEnabled && mappingEnabled && negotiate, config.NegotiatesAnyHistory());
    }

    /// <summary>
    /// A Sync entry whose mapping list is null negotiates nothing.
    /// True: an entry saved without mappings is read as having none.
    /// False: a History run would throw on an entry with no mappings.
    /// </summary>
    [Fact]
    public void NegotiatesHistory_NullMappingList_IsFalse()
    {
        var server = Server("a", ServerMode.Sync);
        server.LibraryMappings = null!;
        var config = With(server);

        Assert.False(config.NegotiatesHistory("a", "lib"));
        Assert.False(config.NegotiatesAnyHistory());
        Assert.Empty(config.GetAllLibraryMappings());
    }

    /// <summary>
    /// The enabled mappings come from sources only, while the full list also holds disabled ones and other Directions.
    /// True: a scan reads only what is on, and disk space still sees every mapped folder.
    /// False: a scan would read a disabled mapping, or a Push server's folder would be missing from disk space.
    /// </summary>
    [Fact]
    public void Mappings_EnabledAndAll_DifferByDirectionAndSwitch()
    {
        var pull = Server("pull", ServerMode.Pull);
        pull.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "on", IsEnabled = true });
        pull.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "off", IsEnabled = false });
        pull.UserMappings.Add(new UserMapping { SourceUserId = "u-on", LocalUserId = "l", IsEnabled = true });
        pull.UserMappings.Add(new UserMapping { SourceUserId = "u-off", LocalUserId = "l", IsEnabled = false });
        var push = Server("push", ServerMode.Push);
        push.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "push", IsEnabled = true });
        push.UserMappings.Add(new UserMapping { SourceUserId = "u-push", LocalUserId = "l", IsEnabled = true });
        var config = With(pull, push);

        var enabledLibraries = config.GetEnabledLibraryMappings().Select(m => m.SourceLibraryId).ToArray();
        var enabledUsers = config.GetEnabledUserMappings().Select(m => m.SourceUserId).ToArray();
        var allLibraries = config.GetAllLibraryMappings().Select(m => m.SourceLibraryId).ToArray();

        Assert.Equal(new[] { "on" }, enabledLibraries);
        Assert.Equal(new[] { "u-on" }, enabledUsers);
        Assert.Equal(new[] { "on", "off", "push" }, allLibraries);
    }

    private static PluginConfiguration With(params SourceServer[] servers) => new() { Servers = new List<SourceServer>(servers) };

    private static SourceServer Server(string key, ServerMode mode) => new()
    {
        Key = key,
        Name = key,
        Url = "http://" + key + ":8096",
        ApiKey = "key",
        ServerId = PeerId,
        Mode = mode
    };
}
