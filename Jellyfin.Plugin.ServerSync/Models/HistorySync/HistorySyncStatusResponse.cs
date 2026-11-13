using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// Response containing history sync status counts.
/// Extends BaseSyncStatusResponse with no additional properties.
/// </summary>
public class HistorySyncStatusResponse : BaseSyncStatusResponse
{
    // History sync uses only the base status counts.
}
