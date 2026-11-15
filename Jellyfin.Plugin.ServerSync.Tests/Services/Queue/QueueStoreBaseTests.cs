using System;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The queue store base opens the database on first use, runs reads and writes on it, and maps times and nulls the way every queue table stores them.
/// </summary>
public sealed class QueueStoreBaseTests : IDisposable
{
    private readonly QueueTestDatabase _db = new();

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A missing provider or logger is refused at construction.
    /// True: a wiring mistake fails at startup.
    /// False: it fails on the first hint, with nothing to say why.
    /// </summary>
    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new QueueStoreProbe(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new QueueStoreProbe(_db.Provider, null!));
    }

    /// <summary>
    /// The database is not opened until a call needs it.
    /// True: a database that cannot be opened fails the call that needs it, not the plugin's startup.
    /// False: one bad database file stops Jellyfin from loading the plugin at all.
    /// </summary>
    [Fact]
    public void Constructor_BeforeAnyCall_DoesNotOpenTheDatabase()
    {
        var (provider, stub) = InterfaceStub.Create<ISyncDatabaseProvider>();
        stub.Answer("get_Database", _ => _db.Database);

        var probe = new QueueStoreProbe(provider, NullLogger.Instance);
        var before = stub.Calls.Count;
        probe.RunRead(conn => 0);

        Assert.Equal(0, before);
        Assert.NotEmpty(stub.Calls);
    }

    /// <summary>
    /// The logger given at construction is the one the store keeps.
    /// True: a store's failures are written under its own name.
    /// False: they are lost or written under another store's name.
    /// </summary>
    [Fact]
    public void Logger_AfterConstruction_IsTheOneGiven()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);

        Assert.Same(NullLogger.Instance, probe.KeptLogger);
    }

    /// <summary>
    /// A read or write with no statements is refused.
    /// True: a caller's mistake is named at once.
    /// False: it surfaces as a null reference while the write lock is held.
    /// </summary>
    [Fact]
    public void ReadAndWrite_NullDelegate_Throw()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);

        Assert.Throws<ArgumentNullException>(() => probe.RunRead<int>(null!));
        Assert.Throws<ArgumentNullException>(() => probe.RunWrite<int>(null!));
        Assert.Throws<ArgumentNullException>(() => probe.RunWrite((Action<Microsoft.Data.Sqlite.SqliteConnection>)null!));
    }

    /// <summary>
    /// A write returns its value, and a read on the read connection sees what was written.
    /// True: a count of changed rows reaches the caller and the dashboard reads current rows.
    /// False: writes are invisible to readers, or their counts are lost.
    /// </summary>
    [Fact]
    public void Write_ThenRead_SeesTheWrittenRow()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);

        var written = probe.RunWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ObjectVersions (Kind, Key, ServerId, Timestamp) VALUES (1, 'probe', 'server', @at)";
            QueueStoreProbe.AddTo(cmd, "@at", QueueStoreProbe.StampOf(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));
            return cmd.ExecuteNonQuery();
        });
        var read = probe.RunRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ObjectVersions WHERE Key = 'probe'";
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        Assert.Equal(1, written);
        Assert.Equal(1, read);
    }

    /// <summary>
    /// A write that returns nothing still runs on the write connection.
    /// True: a plain update lands.
    /// False: the overload without a result silently does nothing.
    /// </summary>
    [Fact]
    public void Write_WithoutAResult_RunsTheStatements()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);

        probe.RunWrite(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ObjectVersions (Kind, Key, ServerId, Timestamp) VALUES (1, 'plain', 'server', '2026-10-03T12:00:00.0000000Z')";
            cmd.ExecuteNonQuery();
        });

        var read = probe.RunRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ServerId FROM ObjectVersions WHERE Key = 'plain'";
            return cmd.ExecuteScalar() as string;
        });
        Assert.Equal("server", read);
    }

    /// <summary>
    /// A stored time parses back to the same UTC instant.
    /// True: a stamp read from a table equals the stamp written, so completions match their rows.
    /// False: a round trip shifts the time and a row never matches the version it was sent with.
    /// </summary>
    [Fact]
    public void Stamp_ThenUnstamp_GivesTheSameUtcInstant()
    {
        var at = new DateTime(2026, 10, 3, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);

        var back = QueueStoreProbe.UnstampOf(QueueStoreProbe.StampOf(at));

        Assert.Equal(at, back);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
    }

    /// <summary>
    /// Stored times are fixed width text whose order is time order, whatever kind the time came as.
    /// True: SQL comparisons on stored times pick the newer stamp.
    /// False: "due before now" and "newer than" would compare text that does not sort by time.
    /// </summary>
    [Fact]
    public void Stamp_EarlierAndLater_OrderAsText()
    {
        var earlier = QueueStoreProbe.StampOf(new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Unspecified));
        var later = QueueStoreProbe.StampOf(new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc).AddTicks(1));

        Assert.Equal(earlier.Length, later.Length);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.EndsWith("Z", earlier, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text that is not a time is refused.
    /// True: a damaged row is reported, not read as some default date.
    /// False: a broken stamp becomes year one and loses every conflict silently.
    /// </summary>
    [Fact]
    public void Unstamp_NotATime_Throws()
    {
        Assert.Throws<FormatException>(() => QueueStoreProbe.UnstampOf("not a time"));
    }

    /// <summary>
    /// A null column reads as null and a filled one as its value, for text and for times.
    /// True: a row never sent has no sent time, and one that was has its own.
    /// False: reading an empty column throws and the whole queue view fails.
    /// </summary>
    [Fact]
    public void TextAndTime_NullAndFilledColumns_ReadAsNullAndValue()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);
        var at = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        var (empty, text, noTime, time) = probe.RunRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT NULL AS Empty, 'value' AS Filled, @at AS At";
            QueueStoreProbe.AddTo(cmd, "@at", QueueStoreProbe.StampOf(at));
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (QueueStoreProbe.TextOf(reader, "Empty"), QueueStoreProbe.TextOf(reader, "Filled"), QueueStoreProbe.TimeOf(reader, "Empty"), QueueStoreProbe.TimeOf(reader, "At"));
        });

        Assert.Null(empty);
        Assert.Equal("value", text);
        Assert.Null(noTime);
        Assert.Equal(at, time);
    }

    /// <summary>
    /// A null parameter is stored as a database null.
    /// True: an optional field that is missing stays missing.
    /// False: the command throws on a null value, or stores the text "null".
    /// </summary>
    [Fact]
    public void Add_NullValue_BindsADatabaseNull()
    {
        var probe = new QueueStoreProbe(_db.Provider, NullLogger.Instance);

        var isNull = probe.RunRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @value IS NULL";
            QueueStoreProbe.AddTo(cmd, "@value", null);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        Assert.Equal(1, isNull);
    }

    /// <summary>
    /// The helpers refuse a missing row or command.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference deep in a query.
    /// </summary>
    [Fact]
    public void Helpers_NullReaderOrCommand_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => QueueStoreProbe.TextOf(null!, "Column"));
        Assert.Throws<ArgumentNullException>(() => QueueStoreProbe.AddTo(null!, "@name", 1));
    }
}
