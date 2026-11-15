using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Configuration;

/// <summary>
/// The manager must hand back a typed API Key as it is, decrypt a stored one, and name this server.
/// </summary>
public sealed class PluginConfigurationManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SecretProtector _secrets;
    private readonly ServerConfiguration _serverConfiguration = new();
    private readonly PluginConfigurationManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationManagerTests"/> class.
    /// </summary>
    public PluginConfigurationManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-configmanager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _secrets = ClientSecretKeys.Protector(_tempDir);
        _manager = new PluginConfigurationManager(Paths(), ServerConfig(), _secrets);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when Jellyfin builds its services.
    /// False: it would fail on the first settings read.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginConfigurationManager(null!, ServerConfig(), _secrets));
        Assert.Throws<ArgumentNullException>(() => new PluginConfigurationManager(Paths(), null!, _secrets));
        Assert.Throws<ArgumentNullException>(() => new PluginConfigurationManager(Paths(), ServerConfig(), null!));
    }

    /// <summary>
    /// A stored key decrypts to the key that was saved, and a plain key comes back as it is.
    /// True: a key saved before encryption was available still works.
    /// False: an old plain key would be unreadable after an update.
    /// </summary>
    [Fact]
    public void DecryptApiKey_StoredAndPlainKey_ReturnThePlainKey()
    {
        var stored = _secrets.Protect("the-api-key");

        Assert.True(SecretProtector.IsProtected(stored));
        Assert.Equal("the-api-key", _manager.DecryptApiKey(stored));
        Assert.Equal("typed-plain", _manager.DecryptApiKey("typed-plain"));
    }

    /// <summary>
    /// A key typed into the page is used as typed, for any server and address, and a missing one is empty.
    /// True: testing a new server or a new key uses exactly what the operator entered.
    /// False: a typed key would be swapped for a stored one, or a missing key would throw.
    /// </summary>
    [Theory]
    [InlineData("typed-key", "typed-key")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ResolveRequestApiKey_TypedKey_IsReturnedAsTyped(string? typed, string expected)
    {
        var resolved = _manager.ResolveRequestApiKey(typed, "any-server", "https://anywhere.example");

        Assert.Equal(expected, resolved);
    }

    /// <summary>
    /// This server's name is the one set in Jellyfin, and the machine's name when none is set.
    /// True: a peer sees a name for this server either way.
    /// False: a server with no name set would send an empty device name.
    /// </summary>
    [Theory]
    [InlineData("Living Room")]
    [InlineData(null)]
    public void LocalServerName_SetOrUnset_IsTheServerNameOrTheMachineName(string? serverName)
    {
        _serverConfiguration.ServerName = serverName!;

        var name = _manager.LocalServerName;

        Assert.Equal(serverName ?? Environment.MachineName, name);
    }

    private static IApplicationPaths Paths() => InterfaceStub.Create<IApplicationPaths>().Proxy;

    private IServerConfigurationManager ServerConfig()
    {
        var (manager, stub) = InterfaceStub.Create<IServerConfigurationManager>();
        stub.Answer("get_" + nameof(IServerConfigurationManager.Configuration), _ => _serverConfiguration);
        return manager;
    }
}
