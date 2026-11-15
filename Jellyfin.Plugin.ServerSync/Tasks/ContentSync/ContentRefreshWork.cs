using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Sdk.Generated.Models;

namespace Jellyfin.Plugin.ServerSync.Tasks.ContentSync;

/// <summary>
/// Source side work item for the Content refresh task, which <see cref="UpdateSyncTablesTask"/> turns into a sync row.
/// </summary>
/// <param name="Source">The server the item came from.</param>
/// <param name="Mapping">The library mapping the item belongs to.</param>
/// <param name="SourceItem">The item as the source reports it.</param>
/// <param name="WatchedByAll">Whether every user picked for the watched filter has played the item, so its row is <see cref="SyncStatus.Ignored"/>.</param>
public sealed record ContentRefreshWork(ScanSource Source, LibraryMapping Mapping, BaseItemDto SourceItem, bool WatchedByAll);
