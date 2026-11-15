using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A real <see cref="ParentDirectory"/> and <see cref="PeerMetadataSource"/> over stub sources, each a server entry this server pulls from with one mapped library.
/// </summary>
internal sealed class ProviderTestHarness
{
    /// <summary>The id this server reports as its own.</summary>
    public const string ThisServerId = "this-server-id";

    /// <summary>The local root every added source maps.</summary>
    public const string LocalRoot = "/local/movies";

    /// <summary>The source root every added source maps.</summary>
    public const string SourceRoot = "/src/movies";

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderTestHarness"/> class with no sources.
    /// </summary>
    public ProviderTestHarness()
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => ThisServerId);
        var (libraryManager, _) = InterfaceStub.Create<ILibraryManager>();
        Directory = new ParentDirectory(Config, Factory, host, NullLogger<ParentDirectory>.Instance);
        Source = new PeerMetadataSource(Directory, Factory, libraryManager, NullLogger<PeerMetadataSource>.Instance);
    }

    /// <summary>Gets the configuration the sources are listed in.</summary>
    public FakeConfigManager Config { get; } = new();

    /// <summary>Gets the factory that builds a client for each source.</summary>
    public ProviderClientFactory Factory { get; } = new();

    /// <summary>Gets the directory under test.</summary>
    public ParentDirectory Directory { get; }

    /// <summary>Gets the metadata source under test.</summary>
    public PeerMetadataSource Source { get; }

    /// <summary>Writes the capabilities a compatible Server Sync answers with.</summary>
    /// <param name="serverId">The id the source reports.</param>
    /// <returns>The capabilities as JSON.</returns>
    public static string Capabilities(string serverId)
        => "{\"ServerId\":\"" + serverId + "\",\"PluginVersion\":\"9.9.9.9\",\"ProtocolVersion\":1,\"MinProtocolVersion\":1}";

    /// <summary>Writes the answer of a source that holds the item asked for.</summary>
    /// <param name="itemId">The item's id on the source.</param>
    /// <returns>The lookup answer as JSON.</returns>
    public static string FoundItem(Guid itemId)
        => "{\"Found\":true,\"ItemId\":\"" + itemId.ToString("N", CultureInfo.InvariantCulture) + "\",\"ItemType\":\"Movie\"}";

    /// <summary>Adds a source this server pulls from, which answers as a compatible Server Sync.</summary>
    /// <param name="key">The server entry's key, which also names its host.</param>
    /// <param name="name">The server entry's name.</param>
    /// <returns>The stub server behind the entry, for the test to add routes to.</returns>
    public ClientStubServer AddSource(string key, string? name = null)
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Capabilities", Capabilities(key + "-id"));
        Factory.Handlers[key] = server;
        Config.Configuration.Servers.Add(new SourceServer
        {
            Key = key,
            Name = name ?? "Server " + key,
            Url = "https://" + key + ".example",
            ApiKey = "k",
            Mode = ServerMode.Pull,
            LibraryMappings = new List<LibraryMapping>
            {
                new() { SourceLibraryId = "lib", SourceRootPath = SourceRoot, LocalRootPath = LocalRoot, IsEnabled = true }
            }
        });
        return server;
    }

    /// <summary>Adds a compatible source that holds one item, and answers both the lookup for it and the read of it.</summary>
    /// <param name="key">The server entry's key, which also names its host.</param>
    /// <param name="itemId">The item's id on the source.</param>
    /// <param name="itemType">The item's type name on the source.</param>
    /// <param name="itemName">The name the source gives the item.</param>
    /// <returns>The stub server behind the entry.</returns>
    public ClientStubServer AddSourceHolding(string key, Guid itemId, string itemType, string itemName)
        => AddSource(key)
            .OnJson("/Sync/Peer/Lookup", FoundItem(itemId))
            .OnJson("/Items", ClientJson.Page(1, ClientJson.Item(itemId, itemType, name: itemName)));

    /// <summary>Finds the server entry added under a key.</summary>
    /// <param name="key">The server entry's key.</param>
    /// <returns>The entry.</returns>
    public SourceServer Entry(string key) => Config.Configuration.Servers.Find(s => s.Key == key)!;
}
