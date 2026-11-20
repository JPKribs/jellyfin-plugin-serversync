using System;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds the server entry, hints, and answers the queue tests share, with one mapped library and one mapped user.
/// </summary>
public static class QueueSamples
{
    /// <summary>The id the sample origin answers to.</summary>
    public const string OriginId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";

    /// <summary>The library root on the origin.</summary>
    public const string SourceRoot = "/media/movies";

    /// <summary>The library root on this server.</summary>
    public const string LocalRoot = "/local/movies";

    /// <summary>The item the sample hints name on the origin.</summary>
    public static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");

    /// <summary>The mapped user on the origin.</summary>
    public static readonly Guid UserId = Guid.Parse("22222222222222222222222222222222");

    /// <summary>The mapped user on this server.</summary>
    public static readonly Guid LocalUserId = Guid.Parse("33333333333333333333333333333333");

    /// <summary>Builds a configured server entry with the sample library and user mapped.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="mode">Which way the entry syncs.</param>
    /// <param name="serverId">The id the server answers to.</param>
    /// <returns>The entry.</returns>
    public static SourceServer Server(string key = "origin", ServerMode mode = ServerMode.Sync, string serverId = OriginId) => new()
    {
        Key = key,
        Name = "Server " + key,
        Url = "http://" + key + ":8096",
        ApiKey = "key",
        ServerId = serverId,
        Mode = mode,
        LibraryMappings =
        {
            new LibraryMapping { SourceLibraryId = "lib", SourceRootPath = SourceRoot, LocalLibraryId = "local", LocalRootPath = LocalRoot }
        },
        UserMappings =
        {
            new UserMapping { SourceUserId = UserId.ToString("N"), LocalUserId = LocalUserId.ToString("N") }
        }
    };

    /// <summary>Gives the local path a file in the sample library maps to.</summary>
    /// <param name="name">The file name under the library root.</param>
    /// <returns>The local path.</returns>
    public static string LocalPath(string name) => PathUtilities.TranslatePath(SourceRoot + "/" + name, SourceRoot, LocalRoot);

    /// <summary>Builds a hint the sample origin made itself.</summary>
    /// <param name="kind">The sync type.</param>
    /// <param name="path">The item's path on the origin.</param>
    /// <param name="key">The key, or null for the sample item's id.</param>
    /// <param name="at">The stamp's time, or null for a minute ago.</param>
    /// <param name="id">The part of the hint id after the origin's id.</param>
    /// <returns>The hint.</returns>
    public static SyncHint Hint(HintKind kind, string? path, string? key = null, DateTime? at = null, string id = "1") => new()
    {
        HintId = OriginId + ":" + id,
        OriginServerId = OriginId,
        Kind = kind,
        Key = key ?? ItemId.ToString("N"),
        ItemPath = path,
        VersionServerId = OriginId,
        VersionTimestamp = at ?? DateTime.UtcNow.AddMinutes(-1)
    };

    /// <summary>Builds a received row from a sample hint.</summary>
    /// <param name="kind">The sync type.</param>
    /// <param name="path">The item's path on the origin.</param>
    /// <param name="key">The key, or null for the sample item's id.</param>
    /// <param name="at">The stamp's time, or null for a minute ago.</param>
    /// <param name="id">The part of the hint id after the origin's id.</param>
    /// <returns>The row, received now.</returns>
    public static InboundHint Inbound(HintKind kind, string? path, string? key = null, DateTime? at = null, string id = "1")
        => InboundHint.FromHint(Hint(kind, path, key, at, id), DateTime.UtcNow);

    /// <summary>Builds the answer Jellyfin gives for a page of one item.</summary>
    /// <param name="id">The item's id.</param>
    /// <param name="path">The item's path, or null to leave it out.</param>
    /// <returns>The JSON.</returns>
    public static string ItemsJson(Guid id, string? path)
        => "{\"Items\":[{\"Id\":\"" + id.ToString("D") + "\",\"Name\":\"Item\"" + (path is null ? string.Empty : ",\"Path\":\"" + path + "\"") + "}],\"TotalRecordCount\":1,\"StartIndex\":0}";

    /// <summary>The answer Jellyfin gives for a page with no items.</summary>
    public const string NoItemsJson = "{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":0}";

    /// <summary>A real stamp resolver over a test database, for handlers that read a peer's stamps.</summary>
    /// <param name="provider">Opens the test database.</param>
    /// <param name="config">The configuration the resolver reads the plugin version from.</param>
    /// <param name="systemId">This server's id.</param>
    /// <returns>The resolver.</returns>
    public static VersionConflictResolver Resolver(ISyncDatabaseProvider provider, FakeConfigManager config, string systemId = "this-server")
        => new(new VersionStore(provider, NullLogger<VersionStore>.Instance), Host(systemId), config, NullLogger<VersionConflictResolver>.Instance);

    /// <summary>Builds a stand-in for the application host that answers one server id.</summary>
    /// <param name="systemId">The id this server answers to.</param>
    /// <returns>The host.</returns>
    public static IServerApplicationHost Host(string systemId = "this-server")
    {
        var (host, stub) = InterfaceStub.Create<IServerApplicationHost>();
        stub.Answer("get_SystemId", _ => systemId);
        return host;
    }

    /// <summary>Builds a configuration manager with every sync type on.</summary>
    /// <returns>The manager.</returns>
    public static FakeConfigManager Config()
    {
        var manager = new FakeConfigManager();
        Enable(manager.Configuration);
        return manager;
    }

    /// <summary>Turns every sync type on.</summary>
    /// <param name="config">The configuration to change.</param>
    public static void Enable(PluginConfiguration config)
    {
        config.EnableMetadataSync = true;
        config.EnableContentSync = true;
        config.EnableHistorySync = true;
        config.EnablePeopleSync = true;
        config.EnablePlaylistSync = true;
    }
}
