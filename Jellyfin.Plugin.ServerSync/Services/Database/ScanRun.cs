using System;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// What one scan of a sync type found.
/// </summary>
/// <param name="FinishedAt">When the scan finished.</param>
/// <param name="Added">How many items the scan tracked for the first time.</param>
/// <param name="Updated">How many items already in step the scan found changed.</param>
/// <param name="Removed">How many items the scan stopped tracking.</param>
public sealed record ScanRun(DateTime FinishedAt, int Added, int Updated, int Removed);
