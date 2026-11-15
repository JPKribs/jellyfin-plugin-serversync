using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Decides on arrival whether a hint can apply here, so one that never will is answered at once and not stored.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HintTriage
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILocalItems _local;
    private readonly VersionStore _versions;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintTriage"/> class.
    /// </summary>
    /// <param name="configManager">Tells which sync types are on.</param>
    /// <param name="local">Finds the local item or person a hint names.</param>
    /// <param name="versions">Holds the stamps a hint is weighed against.</param>
    public HintTriage(IPluginConfigurationManager configManager, ILocalItems local, VersionStore versions)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(versions);

        _configManager = configManager;
        _local = local;
        _versions = versions;
    }

    /// <summary>Whether the local object a parked hint waits for exists now.</summary>
    /// <param name="parkKey">The key the hint waits under.</param>
    /// <param name="personName">The person's name, for a people hint.</param>
    /// <returns><c>true</c> when the object exists.</returns>
    public bool Present(string parkKey, string? personName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parkKey);
        if (parkKey.StartsWith("path:", StringComparison.Ordinal))
        {
            return _local.FindByPath(parkKey["path:".Length..]) is not null;
        }

        return !string.IsNullOrWhiteSpace(personName) && _local.GetPerson(personName) is not null;
    }

    /// <summary>Builds the park key of the local object a hint needs and this server does not have yet.</summary>
    /// <param name="origin">The server entry of the origin, whose library mappings translate the path.</param>
    /// <param name="kind">The sync type of the hint.</param>
    /// <param name="originPath">The item's path on the origin.</param>
    /// <param name="key">The hint's key.</param>
    /// <returns>The park key, or null when nothing is missing or the kind needs no local object.</returns>
    public string? MissingLocal(SourceServer origin, HintKind kind, string? originPath, string key)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(key);
        if (kind == HintKind.People)
        {
            return _local.GetPerson(key) is null ? PersonParkKey(key) : null;
        }

        if (kind is not (HintKind.Metadata or HintKind.History) || HintMapping.FindBySourcePath(origin, originPath) is not { } mapping)
        {
            return null;
        }

        var localPath = PathUtilities.TranslatePath(originPath!, mapping.SourceRootPath, mapping.LocalRootPath);
        return _local.FindByPath(localPath) is null ? PathParkKey(localPath) : null;
    }

    /// <summary>Builds the park key for a hint that waits for a local item.</summary>
    /// <param name="localPath">The item's path on this server.</param>
    /// <returns>The park key.</returns>
    public static string PathParkKey(string localPath) => "path:" + localPath;

    /// <summary>Builds the park key for a hint that waits for a local person.</summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The park key.</returns>
    public static string PersonParkKey(string name) => "person:" + HintProtocol.PeopleKey(name);

    /// <summary>Whether a library mapping's filter leaves out a path on the origin.</summary>
    /// <param name="mapping">The library mapping whose filter is checked.</param>
    /// <param name="path">The path on the origin.</param>
    /// <returns><c>true</c> when the path is left out.</returns>
    public static bool IsFiltered(LibraryMapping mapping, string? path)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return !string.IsNullOrEmpty(path)
            && mapping.FilterMode != LibraryFilterMode.AllowAll
            && mapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems);
    }

    /// <summary>The longest id, type or user name a hint may carry.</summary>
    public const int MaxIdLength = 256;

    /// <summary>The longest key or path a hint may carry, where a key is an id or a person's name.</summary>
    public const int MaxPathLength = 4096;

    /// <summary>Whether any field of a hint is longer than this server stores.</summary>
    private static bool TooLong(SyncHint hint)
        => hint.Key.Length > MaxPathLength
            || hint.ItemPath?.Length > MaxPathLength
            || hint.ItemId?.Length > MaxIdLength
            || hint.ItemType?.Length > MaxIdLength
            || hint.UserId?.Length > MaxIdLength
            || hint.UserName?.Length > MaxIdLength
            || hint.OriginServerId?.Length > MaxIdLength
            || hint.VersionServerId?.Length > MaxIdLength;

    /// <summary>Decides what to do with a hint as it arrives.</summary>
    /// <param name="origin">The server entry of the origin, whose mappings the hint must fit.</param>
    /// <param name="hint">The hint to assess.</param>
    /// <param name="senderServerId">The id of the server that sent the request.</param>
    /// <param name="utcNow">This server's time, which bounds the hint's stamp.</param>
    /// <returns>The code the sender is told, with a reason and a park key when they apply.</returns>
    public HintAssessment Assess(SourceServer origin, SyncHint hint, string senderServerId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(senderServerId);

        // A hint is stored as it arrives, so an unbounded field could fill the disk.
        if (hint.Key is not null && TooLong(hint))
        {
            return new(HintCode.Invalid, "a field of the hint is longer than this server accepts");
        }

        if (string.IsNullOrWhiteSpace(hint.Key))
        {
            return new(HintCode.Invalid, "the hint carries no key");
        }

        if (!string.Equals(hint.OriginServerId, senderServerId, StringComparison.OrdinalIgnoreCase))
        {
            return new(HintCode.Invalid, "a hint's origin must be the sender, since hints are never forwarded");
        }

        // A stamp naming another server would let one peer stand in for a third.
        if (!string.Equals(hint.VersionServerId, senderServerId, StringComparison.OrdinalIgnoreCase))
        {
            return new(HintCode.Invalid, "a hint's version must be the sender's own edit");
        }

        // Completions are remembered by hint id, so a peer may only use ids of its own.
        if (hint.HintId?.StartsWith(senderServerId + ":", StringComparison.OrdinalIgnoreCase) != true)
        {
            return new(HintCode.Invalid, "a hint's id must be one the sender made");
        }

        if (HintProtocol.IsFutureVersion(hint.VersionTimestamp, utcNow))
        {
            return new(HintCode.FutureVersion, $"the hint's version is dated more than {HintProtocol.MaxVersionLead.TotalMinutes:0} minutes ahead of this server's clock. Check the clock on the sender");
        }

        var config = _configManager.Configuration;
        return hint.Kind switch
        {
            HintKind.Metadata => AssessItem(origin, hint, config.EnableMetadataSync, "metadata", utcNow),
            HintKind.Content => AssessItem(origin, hint, config.EnableContentSync, "content", utcNow),
            HintKind.History => AssessHistory(origin, hint, config.EnableHistorySync, utcNow),
            HintKind.People => AssessPerson(hint, config.EnablePeopleSync, utcNow),
            HintKind.Playlists => AssessPlaylist(origin, hint, config.EnablePlaylistSync, utcNow),
            HintKind.Users => new(HintCode.Unsupported, "user settings are not announced live, the scheduled Sync Information task carries them"),
            _ => new(HintCode.Unsupported, $"this server does not apply {hint.Kind} hints")
        };
    }

    /// <summary>Assesses a metadata or content hint against the mappings, the filter, and the local item.</summary>
    private HintAssessment AssessItem(SourceServer origin, SyncHint hint, bool enabled, string module, DateTime utcNow)
    {
        if (!enabled)
        {
            return new(HintCode.ModuleOff, module + " sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out _))
        {
            return new(HintCode.Invalid, "the key is not an item id");
        }

        var mapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (mapping is null)
        {
            return new(HintCode.NotMapped, "the path is not in a library mapped on this server");
        }

        if (IsFiltered(mapping, hint.ItemPath))
        {
            return new(HintCode.Filtered, "the path is left out by the library mapping's filter on this server");
        }

        if (hint.Kind == HintKind.Content)
        {
            return new(HintCode.Queued, null);
        }

        var localPath = PathUtilities.TranslatePath(hint.ItemPath!, mapping.SourceRootPath, mapping.LocalRootPath);
        var local = _local.FindByPath(localPath);
        if (local is null)
        {
            return new(HintCode.NoLocalItem, "no local item at the mapped path yet. It applies when the item arrives", PathParkKey(localPath));
        }

        return AssessVersion(hint, HintProtocol.MetadataKey(local.Id), utcNow);
    }

    /// <summary>Assesses a history hint against the user mapping, the library mapping, and the local item.</summary>
    private HintAssessment AssessHistory(SourceServer origin, SyncHint hint, bool enabled, DateTime utcNow)
    {
        if (!enabled)
        {
            return new(HintCode.ModuleOff, "history sync is off on this server");
        }

        if (!HintProtocol.TryParseHistoryKey(hint.Key, out var originUserId, out _))
        {
            return new(HintCode.Invalid, "the key is not a user id and item id");
        }

        var userMapping = HintMapping.FindBySourceUser(origin, originUserId);
        if (userMapping is null || !Guid.TryParse(userMapping.LocalUserId, out var localUserId))
        {
            return new(HintCode.NotMapped, "the user is not mapped on this server");
        }

        var mapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (mapping is null)
        {
            return new(HintCode.NotMapped, "the path is not in a library mapped on this server");
        }

        if (IsFiltered(mapping, hint.ItemPath))
        {
            return new(HintCode.Filtered, "the path is left out by the library mapping's filter on this server");
        }

        var localPath = PathUtilities.TranslatePath(hint.ItemPath!, mapping.SourceRootPath, mapping.LocalRootPath);
        var local = _local.FindByPath(localPath);
        if (local is null)
        {
            return new(HintCode.NoLocalItem, "no local item at the mapped path yet. It applies when the item arrives", PathParkKey(localPath));
        }

        return AssessVersion(hint, HintProtocol.HistoryKey(localUserId, local.Id), utcNow);
    }

    /// <summary>Assesses a people hint against the local person.</summary>
    private HintAssessment AssessPerson(SyncHint hint, bool enabled, DateTime utcNow)
    {
        if (!enabled)
        {
            return new(HintCode.ModuleOff, "people sync is off on this server");
        }

        if (_local.GetPerson(hint.Key) is null)
        {
            return new(HintCode.NoLocalItem, "the person does not exist on this server yet. It applies when the person arrives", PersonParkKey(hint.Key));
        }

        return AssessVersion(hint, HintProtocol.PeopleKey(hint.Key), utcNow);
    }

    /// <summary>Assesses a playlist hint against the owner's user mapping.</summary>
    private HintAssessment AssessPlaylist(SourceServer origin, SyncHint hint, bool enabled, DateTime utcNow)
    {
        if (!enabled)
        {
            return new(HintCode.ModuleOff, "playlist sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out var rootId) || !Guid.TryParse(hint.ItemId, out _))
        {
            return new(HintCode.Invalid, "the key is not a playlist id");
        }

        if (!Guid.TryParse(hint.UserId, out var ownerId) || HintMapping.FindBySourceUser(origin, ownerId) is null)
        {
            return new(HintCode.NotMapped, "the playlist's owner is not mapped on this server");
        }

        return origin.Mode == ServerMode.Pull
            ? new(HintCode.Queued, null)
            : AssessVersion(hint, HintProtocol.PlaylistKey(rootId), utcNow);
    }

    /// <summary>Turns away a hint whose stamp this server already holds or has moved past.</summary>
    private HintAssessment AssessVersion(SyncHint hint, string localKey, DateTime utcNow)
    {
        if (_versions.Get(hint.Kind, localKey) is not { } held)
        {
            return new(HintCode.Queued, null);
        }

        var incoming = new ObjectVersion { Kind = hint.Kind, Key = localKey, ServerId = hint.VersionServerId, Timestamp = HintProtocol.BoundVersion(hint.VersionTimestamp, utcNow) };
        if (VersionDecider.SameVersion(held, incoming))
        {
            return new(HintCode.AlreadyCurrent, "this server already holds that version");
        }

        return VersionDecider.IsNewer(held, incoming)
            ? new(HintCode.Superseded, "this server holds a newer edit")
            : new(HintCode.Queued, null);
    }
}
