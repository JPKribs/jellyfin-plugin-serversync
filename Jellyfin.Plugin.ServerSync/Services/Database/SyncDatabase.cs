using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Opens and owns the SQLite database that holds every sync table.
/// </summary>
public class SyncDatabase : IDisposable
{
    private static int _tooOldReported;
    private readonly ILogger<SyncDatabase> _logger;
    private readonly string _dbPath;

    /// <summary>
    /// The most read connections open at once. WAL lets readers run beside the writer, so the
    /// dashboard's polling and the scans' lookups do not queue behind every write.
    /// </summary>
    public const int MaxReadConnections = 4;

    private readonly object _writeLock = new();
    private readonly ConcurrentBag<(SqliteConnection Connection, int Generation)> _idleReaders = new();
    private readonly System.Threading.SemaphoreSlim _readerSlots = new(MaxReadConnections, MaxReadConnections);
    private SqliteConnection? _connection;
    private volatile bool _disposed;

    /// <summary>
    /// Rises each time the database file is replaced, so read connections to the old file are closed and not reused.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncDatabase"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="dataPath">Jellyfin's data folder, which holds the database's own folder.</param>
    public SyncDatabase(ILogger<SyncDatabase> logger, string dataPath)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dataPath);
        _logger = logger;
        var dbDir = Path.Combine(dataPath, "serversync");
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "sync.db");

        _logger.LogDebug("Sync database path: {DbPath} (dir exists: {Exists}, writable: {Writable})",
            _dbPath,
            Directory.Exists(dbDir),
            IsDirectoryWritable(dbDir));

        InitializeDatabase();
    }

    /// <summary>
    /// Gets the write connection every sync table shares, reopening it when it is closed.
    /// </summary>
    internal SqliteConnection Connection
    {
        get
        {
            ThrowIfDisposed();
            EnsureConnection();
            return _connection!;
        }
    }

    /// <summary>
    /// Gets the lock every sync table takes to serialize writes on the write connection.
    /// </summary>
    internal object WriteLock => _writeLock;

    /// <summary>
    /// Runs a read on a read connection of its own, so it does not wait for the write lock. A read made while
    /// this thread holds the write lock runs on the write connection, the only one that sees an open transaction's rows.
    /// </summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="read">The read to run against the connection it is given.</param>
    /// <returns>What the read returned.</returns>
    internal T RunRead<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (System.Threading.Monitor.IsEntered(_writeLock))
        {
            return read(Connection);
        }

        ThrowIfDisposed();
        _readerSlots.Wait();
        SqliteConnection? reader = null;
        var generation = System.Threading.Volatile.Read(ref _generation);
        var healthy = false;
        try
        {
            reader = RentReader(generation);
            var result = read(reader);
            healthy = true;
            return result;
        }
        finally
        {
            ReturnReader(reader, generation, healthy);
            _readerSlots.Release();
        }
    }

    private SqliteConnection RentReader(int generation)
    {
        while (_idleReaders.TryTake(out var idle))
        {
            if (idle.Generation == generation && idle.Connection.State == ConnectionState.Open)
            {
                return idle.Connection;
            }

            idle.Connection.Dispose();
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=1;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Keeps a read connection for the next read, or closes one that failed, belongs to a replaced file, or outlived the database.
    /// </summary>
    private void ReturnReader(SqliteConnection? reader, int generation, bool healthy)
    {
        if (reader is null)
        {
            return;
        }

        if (healthy && !_disposed && generation == System.Threading.Volatile.Read(ref _generation))
        {
            _idleReaders.Add((reader, generation));
            return;
        }

        reader.Dispose();
    }

    /// <summary>
    /// Waits for every read in flight and holds new ones back while the file is replaced, since an open handle keeps Windows from moving or deleting it.
    /// </summary>
    private void WithReadersDrained(Action replace)
    {
        var taken = 0;
        try
        {
            for (; taken < MaxReadConnections; taken++)
            {
                if (!_readerSlots.Wait(TimeSpan.FromSeconds(30)))
                {
                    _logger.LogWarning("A read of the sync database is still running after 30 seconds. Replacing the file anyway");
                    break;
                }
            }

            replace();
        }
        finally
        {
            if (taken > 0)
            {
                _readerSlots.Release(taken);
            }
        }
    }

    /// <summary>
    /// Closes every idle read connection and retires the ones in use, for when the file is replaced.
    /// </summary>
    private void DropReaders()
    {
        System.Threading.Interlocked.Increment(ref _generation);
        while (_idleReaders.TryTake(out var idle))
        {
            idle.Connection.Dispose();
        }
    }

    /// <summary>
    /// Throws ObjectDisposedException if the database has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncDatabase), "The sync database has been disposed");
        }
    }

    /// <summary>
    /// Checks if a directory is writable by attempting to create a temp file.
    /// </summary>
    private static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            var testPath = Path.Combine(dirPath, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the connection string, with pooling off so no stale connection fails as read-only after a restart or crash.
    /// The builder quotes the path, so a data folder holding a semicolon or a quote cannot break the string apart.
    /// </summary>
    private string BuildConnectionString()
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    /// <summary>
    /// Opens a connection with the pragmas every connection here relies on. The connection is
    /// disposed when opening or the pragmas fail, so a failed open never leaves a handle behind.
    /// </summary>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(BuildConnectionString());
        try
        {
            connection.Open();

            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes and releases the current connection, if any.
    /// </summary>
    private void CloseConnection()
    {
        DropReaders();
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>
    /// Deletes a file with retry logic for locked files.
    /// </summary>
    private void DeleteFileWithRetry(string filePath, int maxRetries = 3)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                _logger.LogDebug(ex, "Failed to delete {FilePath}, retrying ({Attempt}/{Max})", filePath, i + 1, maxRetries);
                System.Threading.Thread.Sleep(50 * (i + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete file {FilePath}", filePath);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes WAL and SHM journal files associated with the database.
    /// </summary>
    private void DeleteWalFiles()
    {
        var walPath = _dbPath + "-wal";
        var shmPath = _dbPath + "-shm";

        try
        {
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete WAL file at {Path}", walPath);
        }

        try
        {
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete SHM file at {Path}", shmPath);
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            _connection = OpenConnection();
            var action = OpenSchema(_connection);
            switch (action)
            {
                case SchemaAction.UsedNewer:
                    // A newer build allows this reader, so the database is used unchanged.
                    _logger.LogInformation(
                        "Sync database is schema v{Found}, newer than this plugin's v{Expected}, and readable by v{MinReader} and later. Using it as is",
                        DatabaseSchema.GetVersion(_connection),
                        SyncSchema.CurrentSchemaVersion,
                        DatabaseSchema.GetMinReaderVersion(_connection));
                    return;

                case SchemaAction.TooNew:
                    // Reading it would fail on every query, so it becomes a backup.
                    _logger.LogError(
                        "Sync database is schema v{Found}, newer than this plugin's v{Expected}, and this version cannot read it. It is kept as a .newer-* backup the newer version can still read, and a fresh database is started",
                        DatabaseSchema.GetVersion(_connection),
                        SyncSchema.CurrentSchemaVersion);
                    RecreateDatabase("newer");
                    return;

            }

            _logger.LogDebug("Sync database initialized at {DbPath} (schema v{Version})", _dbPath, SyncSchema.CurrentSchemaVersion);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11 || ex.SqliteErrorCode == 26)
        {
            // Only a corrupt file is recreated. A transient failure propagates so tracking survives.
            _logger.LogError(ex, "Sync database is corrupt (SQLite error {Code}), attempting recovery", ex.SqliteErrorCode);
            try
            {
                RecreateDatabase("corrupt");
            }
            catch (Exception recreateEx)
            {
                // Any failure here must close the connection and surface as one error.
                _logger.LogError(recreateEx, "Failed to recreate database");
                CloseConnection();
                throw new InvalidOperationException("Unable to initialize or recover sync database", recreateEx);
            }
        }
        catch (SchemaTooOldException ex)
        {
            // Logged once, since every later use fails the same way.
            if (System.Threading.Interlocked.Exchange(ref _tooOldReported, 1) == 0)
            {
                _logger.LogError("{Message}", ex.Message);
            }

            CloseConnection();
            throw;
        }
        catch
        {
            // The connection is closed first, or its handle outlives the failed start.
            CloseConnection();
            throw;
        }
    }

    /// <summary>
    /// Opens the schema with an oldest reader that follows what the file holds, so a release that cannot read links never opens a database with links as usable.
    /// </summary>
    private SchemaAction OpenSchema(SqliteConnection connection)
        => DatabaseSchema.Open(
            connection,
            SyncSchema.CurrentSchemaVersion,
            SyncSchema.MinReaderFor(connection),
            c => SyncSchema.Create((SqliteConnection)c),
            (_, fromVersion) => throw new SchemaTooOldException(fromVersion));

    /// <summary>
    /// Closes the database, moves it aside as a timestamped backup with its journal, and creates a fresh one.
    /// The old file is kept because it carries Ignored rows and pending approvals, and the three newest backups for each reason stay.
    /// </summary>
    /// <param name="reason">The word in the backup's name, <c>corrupt</c> or <c>newer</c>.</param>
    private void RecreateDatabase(string reason)
    {
        WithReadersDrained(() =>
        {
            CloseConnection();
            DatabaseSchema.SetAside(_dbPath, reason, 3, _logger);
        });
        _connection = OpenConnection();
        OpenSchema(_connection);
        _logger.LogInformation("Database recreated with fresh schema v{Version}", SyncSchema.CurrentSchemaVersion);
    }

    /// <summary>
    /// Ensures the database connection is open, reopening if necessary.
    /// </summary>
    private void EnsureConnection()
    {
        if (_connection != null && _connection.State == ConnectionState.Open)
        {
            return;
        }

        try
        {
            _connection?.Close();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            // A connection that cannot close must not stop the reopen.
            _logger.LogWarning(ex, "Error disposing old database connection");
        }

        _connection = null;

        try
        {
            _connection = OpenConnection();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Drops all data and recreates the database with the latest schema.
    /// </summary>
    public void ResetDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting sync database - all tracking data will be lost");

            WithReadersDrained(() =>
            {
                CloseConnection();
                DeleteFileWithRetry(_dbPath);
                DeleteWalFiles();
            });

            InitializeDatabase();
            _logger.LogInformation("Sync database has been reset with fresh schema v{Version}", SyncSchema.CurrentSchemaVersion);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Closes the connections once, marking the database disposed first so no caller races the close.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            lock (_writeLock)
            {
                _disposed = true;

                try
                {
                    CloseConnection();
                }
                catch (Exception ex)
                {
                    // Disposal must never throw.
                    _logger.LogWarning(ex, "Error during database disposal");
                }
            }
        }
    }
}
