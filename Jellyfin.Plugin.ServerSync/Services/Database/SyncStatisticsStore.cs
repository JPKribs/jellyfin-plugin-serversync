#pragma warning disable CA2100 // SQL is internal and parameterized.
using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Keeps what the Statistics tab charts, in the sync database: the last scans of each sync type, and how many link streams started for each server on each day.
/// It holds only what the charts show, so each write trims the rest.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class SyncStatisticsStore : QueueStoreBase
{
    /// <summary>How many scans of each sync type are kept.</summary>
    public const int RunsKept = 10;

    /// <summary>How many days of link streams are kept.</summary>
    public const int DaysKept = 30;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncStatisticsStore"/> class.
    /// </summary>
    /// <param name="databaseProvider">Opens the sync database on first use.</param>
    /// <param name="logger">Writes log entries.</param>
    public SyncStatisticsStore(ISyncDatabaseProvider databaseProvider, ILogger<SyncStatisticsStore> logger)
        : base(databaseProvider, logger)
    {
    }

    /// <summary>
    /// Records one scan of a sync type and drops the scans older than the last <see cref="RunsKept"/>.
    /// </summary>
    /// <param name="module">The sync type's key, such as Content or History.</param>
    /// <param name="run">What the scan found.</param>
    public void RecordRun(string module, ScanRun run)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentNullException.ThrowIfNull(run);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ScanRuns (Module, FinishedAt, Added, Updated, Removed) VALUES (@module, @at, @added, @updated, @removed);
                DELETE FROM ScanRuns WHERE Module = @module AND Id NOT IN (SELECT Id FROM ScanRuns WHERE Module = @module ORDER BY Id DESC LIMIT @keep)";
            Add(cmd, "@module", module);
            Add(cmd, "@at", Stamp(run.FinishedAt));
            Add(cmd, "@added", run.Added);
            Add(cmd, "@updated", run.Updated);
            Add(cmd, "@removed", run.Removed);
            Add(cmd, "@keep", RunsKept);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Lists the last scans of a sync type, oldest first.
    /// </summary>
    /// <param name="module">The sync type's key.</param>
    /// <returns>Up to <see cref="RunsKept"/> scans.</returns>
    public IReadOnlyList<ScanRun> Runs(string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FinishedAt, Added, Updated, Removed FROM (SELECT * FROM ScanRuns WHERE Module = @module ORDER BY Id DESC LIMIT @keep) ORDER BY Id";
            Add(cmd, "@module", module);
            Add(cmd, "@keep", RunsKept);
            var runs = new List<ScanRun>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(new ScanRun(Unstamp(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            }

            return runs;
        });
    }

    /// <summary>
    /// Counts one link stream started for a server on a day and drops the days older than the last <see cref="DaysKept"/>.
    /// </summary>
    /// <param name="day">The day the stream started, in this server's time.</param>
    /// <param name="serverKey">The key of the server entry the stream was started for.</param>
    public void CountLinkStream(DateOnly day, string serverKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverKey);
        Write(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO LinkStreamDays (Day, ServerKey, Streams) VALUES (@day, @server, 1) ON CONFLICT(Day, ServerKey) DO UPDATE SET Streams = Streams + 1;
                DELETE FROM LinkStreamDays WHERE Day < @oldest";
            Add(cmd, "@day", DayText(day));
            Add(cmd, "@server", serverKey);
            Add(cmd, "@oldest", DayText(day.AddDays(1 - DaysKept)));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Lists the last <see cref="DaysKept"/> days, oldest first and ending today.
    /// </summary>
    /// <param name="today">Today, in this server's time.</param>
    /// <returns>The days.</returns>
    public static IReadOnlyList<DateOnly> Days(DateOnly today)
    {
        var days = new List<DateOnly>(DaysKept);
        for (var day = today.AddDays(1 - DaysKept); day <= today; day = day.AddDays(1))
        {
            days.Add(day);
        }

        return days;
    }

    /// <summary>
    /// Lists how many link streams started for each server on each of the last <see cref="DaysKept"/> days.
    /// A server's counts line up with <see cref="Days"/>, with a zero for a day that had none.
    /// </summary>
    /// <param name="today">Today, in this server's time.</param>
    /// <returns>The counts of each server that had a stream in those days, by server key.</returns>
    public IReadOnlyDictionary<string, int[]> LinkStreams(DateOnly today)
    {
        var days = Days(today);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < days.Count; i++)
        {
            index[DayText(days[i])] = i;
        }

        return Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Day, ServerKey, Streams FROM LinkStreamDays WHERE Day >= @oldest AND Day <= @today";
            Add(cmd, "@oldest", DayText(days[0]));
            Add(cmd, "@today", DayText(today));
            var byServer = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var server = reader.GetString(1);
                if (!byServer.TryGetValue(server, out var counts))
                {
                    counts = new int[days.Count];
                    byServer[server] = counts;
                }

                counts[index[reader.GetString(0)]] = reader.GetInt32(2);
            }

            return byServer;
        });
    }

    /// <summary>Writes a day as text whose order is date order.</summary>
    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
