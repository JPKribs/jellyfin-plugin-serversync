using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Mirrors whitelisted source collections onto this server: for each BoxSet
/// whitelist entry, the source collection's leaves are resolved to their
/// already-synced local counterparts (by translated path) and a matching
/// local collection is created or updated. The collection itself is a sync
/// SELECTOR — its members arrive through normal content sync; this task only
/// rebuilds the container so the local server shows the same collection
/// the source has.
/// Mirrored collections are tagged with a provider id holding the source
/// collection's ID; membership of tagged collections tracks the source
/// (items the source dropped are removed locally).
/// </summary>
public class SyncCollectionsTask : IScheduledTask
{
    /// <summary>
    /// Provider-id key marking a local collection as a mirror of a source
    /// collection; the value is the source BoxSet ID ("N" format).
    /// </summary>
    public const string SourceCollectionProviderKey = "ServerSyncSourceCollection";

    private readonly ILogger<SyncCollectionsTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncCollectionsTask(
        ILogger<SyncCollectionsTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager)
    {
        _logger = logger;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
    }

    /// <inheritdoc />
    public string Name => "Sync Collections";

    /// <inheritdoc />
    public string Key => "ServerSyncCollections";

    /// <inheritdoc />
    public string Description => "Mirrors whitelisted source collections locally, containing the synced counterparts of their members.";

    /// <inheritdoc />
    public string Category => "Content Sync";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = _configManager.Configuration;
        if (!config.EnableContentSync || !config.MirrorSyncedCollections)
        {
            return;
        }

        // (collection id -> (mappings that whitelist it)). A collection can be
        // whitelisted under several mappings (movies + shows split across
        // libraries); its local members are gathered across all of them.
        var collectionsByIdN = new Dictionary<string, List<Models.Configuration.LibraryMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in config.GetEnabledLibraryMappings())
        {
            if (mapping.FilterMode != Models.Configuration.LibraryFilterMode.Whitelist || mapping.FilteredItems == null)
            {
                continue;
            }

            foreach (var fi in mapping.FilteredItems)
            {
                if (string.Equals(fi.Type, "BoxSet", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(fi.ItemId, out _))
                {
                    if (!collectionsByIdN.TryGetValue(fi.ItemId, out var list))
                    {
                        list = new List<Models.Configuration.LibraryMapping>();
                        collectionsByIdN[fi.ItemId] = list;
                    }

                    list.Add(mapping);
                }
            }
        }

        if (collectionsByIdN.Count == 0)
        {
            _logger.LogDebug("No whitelisted collections configured; nothing to mirror");
            return;
        }

        // Serialize against Content refresh/sync so membership isn't computed
        // against a half-updated table or mid-download library.
        var moduleMutex = SyncModuleMutex.ForModule("Content");
        await moduleMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);
            var connection = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!connection.Success)
            {
                _logger.LogError("Sync Collections: source connection failed — {Error}", connection.ErrorMessage ?? "unknown");
                return;
            }

            var localBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }) ?? new List<BaseItem>();

            var processed = 0;
            foreach (var (sourceIdN, mappings) in collectionsByIdN)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await MirrorOneAsync(client, Guid.Parse(sourceIdN), sourceIdN, mappings, localBoxSets, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One dead collection (deleted on source, transport fault)
                    // must not stop the rest. Membership is never REMOVED on a
                    // failed read — MirrorOneAsync throws before any diff.
                    _logger.LogWarning(ex, "Sync Collections: failed to mirror collection {Id}; leaving the local mirror untouched", sourceIdN);
                }

                processed++;
                progress.Report(100.0 * processed / collectionsByIdN.Count);
            }
        }
        finally
        {
            moduleMutex.Release();
        }

        progress.Report(100);
    }

    private async Task MirrorOneAsync(
        SourceServerClient client,
        Guid sourceId,
        string sourceIdN,
        List<Models.Configuration.LibraryMapping> mappings,
        IReadOnlyList<BaseItem> localBoxSets,
        CancellationToken cancellationToken)
    {
        // Name comes from the source collection itself; a missing collection
        // throws (GetWhitelistedItemLeavesAsync would too) so a deleted
        // source collection never empties the local mirror.
        var roots = await client.GetItemsByIdsAsync(new[] { sourceId }, new[] { Jellyfin.Sdk.Generated.Models.ItemFields.Path }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = roots.FirstOrDefault(r => r.Id == sourceId);
        if (root == null || string.IsNullOrWhiteSpace(root.Name))
        {
            throw new InvalidOperationException($"Source collection {sourceIdN} no longer exists (remove it from the whitelist to stop mirroring)");
        }

        var leaves = await client.GetWhitelistedItemLeavesAsync(sourceId, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Resolve leaves to local items via each whitelisting mapping's path
        // translation; leaves not yet downloaded (or outside every mapping's
        // root) simply aren't members yet — the next run picks them up.
        var targetIds = new HashSet<Guid>();
        foreach (var leaf in leaves)
        {
            if (string.IsNullOrEmpty(leaf.Path))
            {
                continue;
            }

            foreach (var mapping in mappings)
            {
                var sourceRoot = (mapping.SourceRootPath ?? string.Empty).TrimEnd('/', '\\');
                if (sourceRoot.Length == 0
                    || !leaf.Path.Replace('\\', '/').StartsWith(sourceRoot.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var localPath = PathUtilities.TranslatePath(leaf.Path, mapping.SourceRootPath!, mapping.LocalRootPath);
                var localItem = _libraryManager.FindByPath(localPath, isFolder: false);
                if (localItem != null)
                {
                    targetIds.Add(localItem.Id);
                }

                break;
            }
        }

        var existing = FindLocalMirror(localBoxSets, sourceIdN, root.Name!);
        if (existing == null)
        {
            if (targetIds.Count == 0)
            {
                _logger.LogInformation(
                    "Sync Collections: no synced members for '{Name}' yet; the collection will be created once content sync has downloaded some",
                    root.Name);
                return;
            }

            var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = root.Name,
                IsLocked = true,
                ProviderIds = new Dictionary<string, string> { [SourceCollectionProviderKey] = sourceIdN }
            }).ConfigureAwait(false);

            await _collectionManager.AddToCollectionAsync(created.Id, targetIds).ConfigureAwait(false);
            _logger.LogInformation("Sync Collections: created '{Name}' with {Count} member(s)", root.Name, targetIds.Count);
            return;
        }

        // Adopted-by-name collections get stamped so later renames on the
        // source still find them.
        if (!existing.ProviderIds.ContainsKey(SourceCollectionProviderKey))
        {
            existing.ProviderIds[SourceCollectionProviderKey] = sourceIdN;
            await _libraryManager.UpdateItemAsync(existing, existing.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        var currentIds = new HashSet<Guid>(existing.GetLinkedChildren().Select(c => c.Id));
        var toAdd = targetIds.Where(id => !currentIds.Contains(id)).ToList();
        var toRemove = currentIds.Where(id => !targetIds.Contains(id)).ToList();

        if (toAdd.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(existing.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(existing.Id, toRemove).ConfigureAwait(false);
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            _logger.LogInformation(
                "Sync Collections: '{Name}' updated (+{Added}/-{Removed}, {Total} member(s))",
                existing.Name, toAdd.Count, toRemove.Count, targetIds.Count);
        }
    }

    private static BoxSet? FindLocalMirror(IReadOnlyList<BaseItem> localBoxSets, string sourceIdN, string sourceName)
    {
        BoxSet? byName = null;
        foreach (var item in localBoxSets)
        {
            if (item is not BoxSet boxSet)
            {
                continue;
            }

            if (boxSet.ProviderIds.TryGetValue(SourceCollectionProviderKey, out var tagged)
                && string.Equals(tagged, sourceIdN, StringComparison.OrdinalIgnoreCase))
            {
                return boxSet;
            }

            if (byName == null && string.Equals(boxSet.Name, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                byName = boxSet;
            }
        }

        return byName;
    }
}
