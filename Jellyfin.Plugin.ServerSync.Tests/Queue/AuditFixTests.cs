using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

/// <summary>Tests for the fixes from the performance and data integrity audit of 12.2.1.</summary>
public sealed class AuditFixTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly FixedProvider _provider;

    public AuditFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _provider = new FixedProvider(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A negotiated history base never moves backwards, and a write that carries no base keeps the one held.
    /// True: a refresh that read the row before a hint recorded a newer base cannot write its older copy back.
    /// False: the agreement is rolled back and the next merge reverts a real change.
    /// </summary>
    [Fact]
    public void HistoryBase_NeverMovesBackwards()
    {
        var table = new HistorySyncTableManager(_provider, NullLogger<HistorySyncTableManager>.Instance);
        var newer = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        table.Upsert(Row(negotiatedAt: newer, played: true));
        table.Upsert(Row(negotiatedAt: newer.AddMinutes(-5), played: false));
        var held = table.GetByKey(("su", "si"));
        Assert.Equal(newer, held?.NegotiatedAt);
        Assert.True(held?.NegotiatedIsPlayed);

        table.Upsert(Row(negotiatedAt: null, played: false));
        Assert.Equal(newer, table.GetByKey(("su", "si"))?.NegotiatedAt);

        table.Upsert(Row(negotiatedAt: newer.AddMinutes(1), played: false));
        held = table.GetByKey(("su", "si"));
        Assert.Equal(newer.AddMinutes(1), held?.NegotiatedAt);
        Assert.False(held?.NegotiatedIsPlayed);
    }

    /// <summary>
    /// Rows from before servers became a list are given the first scan server's key, and errored rows that
    /// sat for a day are queued again with fresh retries.
    /// True: removing that server removes its rows, and a peer that restarted does not leave rows errored forever.
    /// False: legacy rows belong to whichever server connects first, and transient failures are permanent.
    /// </summary>
    [Fact]
    public void Maintenance_BackfillsKeysAndRequeuesErrored()
    {
        var table = new HistorySyncTableManager(_provider, NullLogger<HistorySyncTableManager>.Instance);
        var row = Row(negotiatedAt: null, played: true);
        row.ServerKey = null;
        row.Status = SyncStatus.Errored;
        row.RetryCount = 5;
        row.StatusDate = DateTime.UtcNow.AddDays(-2);
        table.Upsert(row);

        var store = new MaintenanceStore(_provider, NullLogger<MaintenanceStore>.Instance);
        Assert.Equal(1, store.BackfillServerKey("first"));
        Assert.Equal(0, store.BackfillServerKey("first"));
        Assert.Equal(1, store.RequeueErrored(DateTime.UtcNow.AddDays(-1)));

        var held = table.GetByKey(("su", "si"));
        Assert.Equal("first", held?.ServerKey);
        Assert.Equal(SyncStatus.Queued, held?.Status);
        Assert.Equal(0, held?.RetryCount);
    }

    /// <summary>
    /// The deepest library root that holds a path is the one meant for it, whatever order the mappings
    /// were added in.
    /// True: a folder mapped on its own is not swallowed by a mapping of the whole drive.
    /// False: a hint maps to the wrong library and lands at the wrong local path.
    /// </summary>
    [Fact]
    public void Mapping_PicksTheMostSpecificRoot()
    {
        var peer = new SourceServer
        {
            LibraryMappings =
            {
                new LibraryMapping { SourceRootPath = "/peer", LocalRootPath = "/local/all", IsEnabled = true },
                new LibraryMapping { SourceRootPath = "/peer/Movies", LocalRootPath = "/local/Movies", IsEnabled = true }
            }
        };
        Assert.Equal("/local/Movies", HintMapping.FindBySourcePath(peer, "/peer/Movies/A/a.mp4")?.LocalRootPath);
        Assert.Equal("/local/all", HintMapping.FindBySourcePath(peer, "/peer/Shows/S/e.mp4")?.LocalRootPath);
        Assert.Equal("/peer/Movies", HintMapping.FindByLocalPath(new SourceServer
        {
            LibraryMappings =
            {
                new LibraryMapping { SourceRootPath = "/peer", LocalRootPath = "/local", IsEnabled = true },
                new LibraryMapping { SourceRootPath = "/peer/Movies", LocalRootPath = "/local/Movies", IsEnabled = true }
            }
        }, "/local/Movies/A/a.mp4")?.SourceRootPath);
    }

    /// <summary>
    /// A file this server wrote while syncing is remembered, so the library pickup does not announce it.
    /// True: a downloaded file is not sent back to every peer as a new file.
    /// False: each download echoes to the origin and the rest of the pool.
    /// </summary>
    [Fact]
    public void WrittenFiles_RememberSyncWrites()
    {
        var path = "/local/Movies/" + Guid.NewGuid().ToString("N") + ".mkv";
        Assert.False(WrittenFiles.WasWritten(path));
        WrittenFiles.Mark(path);
        Assert.True(WrittenFiles.WasWritten(path));
        Assert.True(WrittenFiles.WasWritten(path.ToUpperInvariant()));
        Assert.False(WrittenFiles.WasWritten(null));
    }

    /// <summary>
    /// Two addresses name the same server when scheme, host, port, and path match, whatever their case or
    /// trailing slash.
    /// True: the stored key still reaches its own server after the page normalizes the address.
    /// False: the key goes to an edited address, or a saved address is refused because of a slash.
    /// </summary>
    [Theory]
    [InlineData("http://a:8096", "http://A:8096/", true)]
    [InlineData("http://a:8096/jellyfin", "http://a:8096/jellyfin/", true)]
    [InlineData("http://a", "http://a:80", true)]
    [InlineData("http://a:8096", "http://b:8096", false)]
    [InlineData("http://a:8096", "https://a:8096", false)]
    [InlineData("http://a:8096", "http://a:8097", false)]
    [InlineData("http://a:8096/x", "http://a:8096/y", false)]
    public void SameServerUrl_ComparesTheServer(string a, string b, bool same)
    {
        Assert.Equal(same, ConfigurationUtilities.SameServerUrl(a, b));
    }

    /// <summary>
    /// An IPv4 address written as IPv6 is judged as the IPv4 address it is.
    /// True: "::ffff:127.0.0.1" is refused like 127.0.0.1 when private networks are disallowed.
    /// False: wrapping a loopback address in IPv6 slips past the check.
    /// </summary>
    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]:8096")]
    [InlineData("http://[::ffff:10.0.0.5]:8096")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    public void Ssrf_UnwrapsMappedAddresses(string url)
    {
        Assert.NotNull(ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork: false));
    }

    /// <summary>
    /// A refused pairing is checked again at most once per interval.
    /// True: a stream of requests from a peer held with a standard user's key starts one check, not one each.
    /// False: every request calls the peer back.
    /// </summary>
    [Fact]
    public void Pairing_RecheckIsRateLimited()
    {
        var store = new PeerPairingStore(_provider, new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);
        Assert.True(store.TryClaimRecheck("peer", TimeSpan.FromMinutes(1)));
        Assert.False(store.TryClaimRecheck("peer", TimeSpan.FromMinutes(1)));
        Assert.True(store.TryClaimRecheck("other", TimeSpan.FromMinutes(1)));
        Assert.True(store.TryClaimRecheck("peer", TimeSpan.Zero));
    }

    private static HistorySyncItem Row(DateTime? negotiatedAt, bool played) => new()
    {
        SourceUserId = "su",
        LocalUserId = "lu",
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = "si",
        ItemName = "Item",
        ServerKey = "server",
        Status = SyncStatus.Synced,
        StatusDate = DateTime.UtcNow,
        NegotiatedAt = negotiatedAt,
        NegotiatedIsPlayed = played
    };

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}
