using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.ContentSync;

/// <summary>
/// Mirrors whitelisted source collections onto this server, with each collection holding the local copies of its members.
/// A mirror is tagged with the source collection's id, and its membership follows the source, removals included.
/// </summary>
public class SyncCollectionsTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// The provider id key that marks a local collection as a mirror of a source collection, whose id is the value.
    /// </summary>
    public const string SourceCollectionProviderKey = "ServerSyncSourceCollection";

    private readonly ILogger<SyncCollectionsTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILocalItems _localItems;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCollectionsTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="collectionManager">Creates and updates Jellyfin collections.</param>
    /// <param name="localItems">Finds items in this server's library.</param>
    public SyncCollectionsTask(
        ILogger<SyncCollectionsTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILocalItems localItems)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(collectionManager);
        ArgumentNullException.ThrowIfNull(localItems);

        _logger = logger;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _localItems = localItems;
    }

    /// <inheritdoc />
    public string Name => "Sync Collections";

    /// <inheritdoc />
    public string Key => "ServerSyncCollections";

    /// <inheritdoc />
    public string Description => "Mirrors whitelisted source collections locally, containing the synced counterparts of their members.";

    /// <inheritdoc />
    public string Category => "Content Sync";

    /// <summary>
    /// Runs as the last step of Sync Content, so it leaves the task list like the other sync type steps.
    /// </summary>
    /// <inheritdoc />
    public bool IsHidden => true;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = _configManager.Configuration;
        if (!config.EnableContentSync || !config.MirrorSyncedCollections)
        {
            return;
        }

        // Collection ids are a server's own, so they are grouped by server.
        var work = new List<(SourceServer Server, Dictionary<string, List<LibraryMapping>> Collections)>();
        foreach (var server in config.GetPullServers())
        {
            var collectionsByIdN = new Dictionary<string, List<LibraryMapping>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in server.GetEnabledLibraryMappings())
            {
                if (mapping.FilterMode != LibraryFilterMode.Whitelist || mapping.FilteredItems == null)
                {
                    continue;
                }

                foreach (var fi in mapping.FilteredItems.Where(f => string.Equals(f.Type, "BoxSet", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(f.ItemId, out _)))
                {
                    if (!collectionsByIdN.TryGetValue(fi.ItemId, out var list))
                    {
                        list = new List<LibraryMapping>();
                        collectionsByIdN[fi.ItemId] = list;
                    }

                    list.Add(mapping);
                }
            }

            if (collectionsByIdN.Count > 0)
            {
                work.Add((server, collectionsByIdN));
            }
        }

        var total = work.Sum(w => w.Collections.Count);
        if (total == 0)
        {
            _logger.LogDebug("No whitelisted collections configured. Nothing to mirror");
            return;
        }

        // Held so membership is not computed against a half updated table.
        var moduleMutex = SyncModuleMutex.ForModule("Content");
        await moduleMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var localBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }) ?? new List<BaseItem>();

            var processed = 0;
            foreach (var (server, collectionsByIdN) in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceServerClient client;
                try
                {
                    client = _clientFactory.Create(server);
                }
                catch (ArgumentException ex)
                {
                    // One server's bad URL must not stop the other servers' collections.
                    _logger.LogError("Sync Collections: server '{Server}' rejected: {Error}", server.DisplayName, FormatUtilities.SanitizeForLog(ex.Message, 300));
                    processed += collectionsByIdN.Count;
                    progress.Report(100.0 * processed / total);
                    continue;
                }

                using var clientScope = client;
                var connection = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!connection.Success)
                {
                    _logger.LogError("Sync Collections: connection to '{Server}' failed: {Error}", server.DisplayName, FormatUtilities.SanitizeForLog(connection.ErrorMessage ?? "unknown", 300));
                    processed += collectionsByIdN.Count;
                    progress.Report(100.0 * processed / total);
                    continue;
                }

                foreach (var (sourceIdN, mappings) in collectionsByIdN)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await MirrorOneAsync(client, Guid.Parse(sourceIdN), sourceIdN, mappings, localBoxSets, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One dead collection must not stop the rest.
                        _logger.LogWarning(ex, "Sync Collections: failed to mirror collection {Id} from '{Server}'. Leaving the local mirror untouched", sourceIdN, server.DisplayName);
                    }

                    processed++;
                    progress.Report(100.0 * processed / total);
                }
            }
        }
        finally
        {
            moduleMutex.Release();
        }

        progress.Report(100);
    }

    /// <summary>Creates or updates the local mirror of one source collection, and throws before any change when the source collection is gone, so its mirror is never emptied.</summary>
    private async Task MirrorOneAsync(
        SourceServerClient client,
        Guid sourceId,
        string sourceIdN,
        List<LibraryMapping> mappings,
        IReadOnlyList<BaseItem> localBoxSets,
        CancellationToken cancellationToken)
    {
        var roots = await client.GetItemsByIdsAsync(new[] { sourceId }, new[] { Jellyfin.Sdk.Generated.Models.ItemFields.Path }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = roots.FirstOrDefault(r => r.Id == sourceId);
        if (root == null || string.IsNullOrWhiteSpace(root.Name))
        {
            throw new InvalidOperationException($"Source collection {sourceIdN} no longer exists (remove it from the whitelist to stop mirroring)");
        }

        var leaves = await client.GetWhitelistedItemLeavesAsync(sourceId, cancellationToken: cancellationToken).ConfigureAwait(false);

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
                var localItem = _localItems.FindByPath(localPath, isFolder: false);
                if (localItem != null)
                {
                    targetIds.Add(localItem.Id);
                }

                break;
            }
        }

        var existing = FindLocalMirror(localBoxSets, sourceIdN, root.Name!, out var nameConflict);
        if (nameConflict)
        {
            return;
        }

        if (existing == null)
        {
            if (targetIds.Count == 0)
            {
                _logger.LogInformation(
                    "Sync Collections: no synced members for '{Name}' yet. The collection will be created once content sync has downloaded some",
                    FormatUtilities.SanitizeForLog(root.Name));
                return;
            }

            var created = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = root.Name,
                IsLocked = true,
                ProviderIds = new Dictionary<string, string> { [SourceCollectionProviderKey] = sourceIdN }
            }).ConfigureAwait(false);

            await _collectionManager.AddToCollectionAsync(created.Id, targetIds).ConfigureAwait(false);
            _logger.LogInformation("Sync Collections: created '{Name}' with {Count} member(s)", FormatUtilities.SanitizeForLog(root.Name), targetIds.Count);
            return;
        }

        // Tagged so a rename on the source still finds an adopted collection.
        if (!existing.ProviderIds.ContainsKey(SourceCollectionProviderKey))
        {
            existing.ProviderIds[SourceCollectionProviderKey] = sourceIdN;
            await _libraryManager.UpdateItemAsync(existing, existing.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        var currentIds = new HashSet<Guid>(existing.GetLinkedChildren().Select(c => c.Id));
        var toAdd = targetIds.Where(id => !currentIds.Contains(id)).ToList();
        var toRemove = currentIds.Where(id => !targetIds.Contains(id)).ToList();

        // Nothing resolved locally, which is a lookup failure, so the mirror is kept.
        if (targetIds.Count == 0 && leaves.Count > 0 && toRemove.Count > 0)
        {
            _logger.LogWarning(
                "Sync Collections: '{Name}' has {Count} source member(s) but none resolved to local items. Leaving the local mirror unchanged this run",
                existing.Name, leaves.Count);
            return;
        }

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

    /// <summary>Finds the local mirror of a source collection by its tag, or an empty collection of the same name to adopt.</summary>
    private BoxSet? FindLocalMirror(IReadOnlyList<BaseItem> localBoxSets, string sourceIdN, string sourceName, out bool nameConflict)
    {
        nameConflict = false;
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

        // A hand made collection sharing the name is never adopted or duplicated.
        if (byName != null && byName.GetLinkedChildren().Count > 0)
        {
            nameConflict = true;
            _logger.LogWarning(
                "Sync Collections: a local collection named '{Name}' already exists with its own members. Mirroring is skipped for it. Rename or empty the local collection to let the mirror take over",
                FormatUtilities.SanitizeForLog(sourceName));
            return null;
        }

        return byName;
    }
}
