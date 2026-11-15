using System;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Where a clean scan of one server reached, so the next scan can list only what changed since.
/// </summary>
/// <param name="ScopeHash">A hash of the settings that decide what the scan keeps. A change to them forces a full scan.</param>
/// <param name="ListedFrom">When the last clean scan started.</param>
/// <param name="FullAt">When the last full scan started.</param>
public sealed record ScanWatermark(string ScopeHash, DateTime ListedFrom, DateTime FullAt);
