using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

public readonly record struct HintAssessment(HintCode Code, string? Reason, string? ParkKey = null)
{
    public bool Keep => Code is HintCode.Queued or HintCode.NoLocalItem;
}

[PluginService(ServiceLifetime.Singleton)]
public sealed class HintTriage
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly ILocalItems _local;
    private readonly VersionStore _versions;

    public HintTriage(IPluginConfigurationManager configManager, ILocalItems local, VersionStore versions)
    {
        _configManager = configManager;
        _local = local;
        _versions = versions;
    }

    public bool Present(string parkKey, string? personName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parkKey);
        if (parkKey.StartsWith("path:", StringComparison.Ordinal))
        {
            return _local.FindByPath(parkKey["path:".Length..]) is not null;
        }

        return !string.IsNullOrWhiteSpace(personName) && _local.GetPerson(personName) is not null;
    }

    public string? MissingLocal(SourceServer origin, HintKind kind, string? originPath, string key)
    {
        ArgumentNullException.ThrowIfNull(origin);
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

    public static string PathParkKey(string localPath) => "path:" + localPath;

    public static string PersonParkKey(string name) => "person:" + HintProtocol.PeopleKey(name);

    public static bool IsFiltered(LibraryMapping mapping, string? path)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return !string.IsNullOrEmpty(path)
            && mapping.FilterMode != LibraryFilterMode.AllowAll
            && mapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems);
    }

    public HintAssessment Assess(SourceServer origin, SyncHint hint, string senderServerId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(hint);

        if (string.IsNullOrWhiteSpace(hint.Key))
        {
            return new(HintCode.Invalid, "the hint carries no key");
        }

        if (!string.Equals(hint.OriginServerId, senderServerId, StringComparison.OrdinalIgnoreCase))
        {
            return new(HintCode.Invalid, "a hint's origin must be the sender, since hints are never forwarded");
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
            HintKind.Users => new(HintCode.Unsupported, "user settings are not announced live, the scheduled Sync Information task carries them"),
            _ => new(HintCode.Unsupported, $"this server does not apply {hint.Kind} hints")
        };
    }

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
