#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Where a clean scan of one server reached, so the next scan can list only what changed since.
/// </summary>
/// <param name="ScopeHash">A hash of the settings that decide what the scan keeps. A change to them forces a full scan.</param>
/// <param name="ListedFrom">When the last clean scan started.</param>
/// <param name="FullAt">When the last full scan started.</param>
public sealed record ScanWatermark(string ScopeHash, DateTime ListedFrom, DateTime FullAt);

/// <summary>
/// Keeps one <see cref="ScanWatermark"/> for each scan task and server, in the sync database.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ScanWatermarkStore : QueueStoreBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScanWatermarkStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Database provider.</param>
    /// <param name="logger">Logger.</param>
    public ScanWatermarkStore(ISyncDatabaseProvider databaseProvider, ILogger<ScanWatermarkStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Reads the watermark for a scan task and server.
    /// </summary>
    /// <param name="taskKey">The scan task's key.</param>
    /// <param name="serverKey">The server entry's key.</param>
    /// <returns>The watermark, or null when there is none.</returns>
    public ScanWatermark? Get(string taskKey, string serverKey) => Read(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ScopeHash, ListedFrom, FullAt FROM ScanWatermarks WHERE TaskKey = @task AND ServerKey = @server";
        Add(cmd, "@task", taskKey);
        Add(cmd, "@server", serverKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new ScanWatermark(reader.GetString(0), Unstamp(reader.GetString(1)), Unstamp(reader.GetString(2)))
            : null;
    });

    /// <summary>
    /// Saves the watermark for a scan task and server, replacing any earlier one.
    /// </summary>
    /// <param name="taskKey">The scan task's key.</param>
    /// <param name="serverKey">The server entry's key.</param>
    /// <param name="watermark">The watermark to keep.</param>
    public void Set(string taskKey, string serverKey, ScanWatermark watermark)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ScanWatermarks (TaskKey, ServerKey, ScopeHash, ListedFrom, FullAt) VALUES (@task, @server, @scope, @from, @full)
                ON CONFLICT(TaskKey, ServerKey) DO UPDATE SET ScopeHash = @scope, ListedFrom = @from, FullAt = @full";
            Add(cmd, "@task", taskKey);
            Add(cmd, "@server", serverKey);
            Add(cmd, "@scope", watermark.ScopeHash);
            Add(cmd, "@from", Stamp(watermark.ListedFrom));
            Add(cmd, "@full", Stamp(watermark.FullAt));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Forgets watermarks, so the next scan lists everything.
    /// </summary>
    /// <param name="taskKey">The scan task to forget, or null for every task.</param>
    /// <returns>How many watermarks were removed.</returns>
    public int Clear(string? taskKey = null) => Write(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = taskKey is null ? "DELETE FROM ScanWatermarks" : "DELETE FROM ScanWatermarks WHERE TaskKey = @task";
        if (taskKey is not null)
        {
            Add(cmd, "@task", taskKey);
        }

        return cmd.ExecuteNonQuery();
    });
}
