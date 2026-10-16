#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

public sealed record ScanWatermark(string ScopeHash, DateTime ListedFrom, DateTime FullAt);

[PluginService(ServiceLifetime.Singleton)]
public sealed class ScanWatermarkStore : QueueStoreBase
{
    public ScanWatermarkStore(ISyncDatabaseProvider databaseProvider, ILogger<ScanWatermarkStore> logger)
        : base(databaseProvider, logger)
    {
    }

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
