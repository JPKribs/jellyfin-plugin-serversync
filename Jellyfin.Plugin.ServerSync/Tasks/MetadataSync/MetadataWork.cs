using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Sdk.Generated.Models;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source side work item for the Metadata refresh task, which <see cref="RefreshMetadataSyncTableTask"/> turns into a sync row.
/// </summary>
/// <param name="Source">The server the item came from.</param>
/// <param name="LibraryMapping">The library mapping the item belongs to.</param>
/// <param name="SourceItem">The item as the source reports it.</param>
/// <param name="IsFolder">Whether the item is a container such as a series or album.</param>
public sealed record MetadataWork(ScanSource Source, LibraryMapping LibraryMapping, BaseItemDto SourceItem, bool IsFolder);
