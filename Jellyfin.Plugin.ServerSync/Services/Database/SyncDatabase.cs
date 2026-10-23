using System;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SQLite database for tracking sync items between servers.
/// </summary>
public class SyncDatabase : IDisposable
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly string _dbPath;
    /// <summary>
    /// The most read connections open at once. WAL lets readers run beside the writer, so the
    /// dashboard's polling and the scans' lookups no longer queue behind every write.
    /// </summary>
    public const int MaxReadConnections = 4;

    private readonly object _writeLock = new();
    private readonly ConcurrentBag<(SqliteConnection Connection, int Generation)> _idleReaders = new();
    private readonly System.Threading.SemaphoreSlim _readerSlots = new(MaxReadConnections, MaxReadConnections);
    private SqliteConnection? _connection;
    private volatile bool _disposed;

    // Bumped whenever the database file is replaced, so read connections to the old file are dropped
    // instead of being handed out again.
    private int _generation;

    public SyncDatabase(ILogger<SyncDatabase> logger, string dataPath)
    {
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
    /// Open SQLite connection. Used by per-table <c>SyncTableManager</c> instances.
    /// Throws if disposed. Reopens transparently if closed.
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
    /// Shared write lock, used by per-table managers to serialize mutations
    /// against the same connection.
    /// </summary>
    internal object WriteLock => _writeLock;

    /// <summary>
    /// Runs a read on a read connection of its own, so it does not wait for the write lock. A read
    /// made while this thread holds the write lock runs on the write connection instead, since only
    /// that connection sees the rows of a transaction still open there.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read.</param>
    /// <returns>Its result.</returns>
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

    // A connection that failed, belongs to a replaced file, or outlived the database is closed rather
    // than kept for the next read.
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

    // Waits for every read in flight to finish and holds new ones back while the file is replaced,
    // since an open handle keeps Windows from moving or deleting it.
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

    // Closes every idle read connection and retires the ones in use, for when the file is replaced.
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
    /// Builds the SQLite connection string with hardened settings.
    /// </summary>
    private string BuildConnectionString()
    {
        // Use a connection string with settings for better reliability:
        // - Mode=ReadWriteCreate: Create the file if it doesn't exist
        // - Pooling=False: Disable connection pooling to avoid stale cached connections
        //   causing SQLITE_READONLY errors after server restarts or crashes
        // The builder quotes the path, since a data folder holding a semicolon or a quote would
        // otherwise break the string apart.
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

            // Set pragmas for reliability in multi-threaded environments
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
                System.Threading.Thread.Sleep(50 * (i + 1)); // Brief backoff
            }
            catch (Exception ex)
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
        catch (Exception ex)
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
        catch (Exception ex)
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
                    // A newer build wrote this database and says this one can still use it, since everything
                    // it added since is extra. It is used as is, so the newer build finds it as it left it.
                    _logger.LogInformation(
                        "Sync database is schema v{Found}, newer than this plugin's v{Expected}, and readable by v{MinReader} and later. Using it as is",
                        DatabaseSchema.GetVersion(_connection),
                        DatabaseMigrationService.CurrentSchemaVersion,
                        DatabaseSchema.GetMinReaderVersion(_connection));
                    return;

                case SchemaAction.TooNew:
                    // A newer build wrote this database and this one cannot read it. Carrying on would produce
                    // a stream of confusing per query errors, so it is kept as a backup the newer build can
                    // still read, and this build starts fresh.
                    _logger.LogError(
                        "Sync database is schema v{Found}, newer than this plugin's v{Expected}, and this version cannot read it. It is kept as a .newer-* backup the newer version can still read, and a fresh database is started",
                        DatabaseSchema.GetVersion(_connection),
                        DatabaseMigrationService.CurrentSchemaVersion);
                    RecreateDatabase("newer");
                    return;

                case SchemaAction.MigrationFailed:
                    _logger.LogWarning("Migration failed. The database is kept as a .corrupt-* backup and a fresh one is started");
                    RecreateDatabase("corrupt");
                    return;
            }

            _logger.LogDebug("Sync database initialized at {DbPath} (schema v{Version})", _dbPath, DatabaseMigrationService.CurrentSchemaVersion);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11 || ex.SqliteErrorCode == 26)
        {
            // SQLITE_CORRUPT / SQLITE_NOTADB: the file itself is unusable, so recreation is the only way
            // forward. Anything else, a locked file at boot, a permissions hiccup, a full disk, is transient.
            // Recreating would reset every tracking table and the user's Ignored and approval state over a
            // condition that fixes itself, so those propagate and the plugin retries on the next start.
            _logger.LogError(ex, "Sync database is corrupt (SQLite error {Code}), attempting recovery", ex.SqliteErrorCode);
            try
            {
                RecreateDatabase("corrupt");
            }
            catch (Exception recreateEx)
            {
                _logger.LogError(recreateEx, "Failed to recreate database");
                CloseConnection();
                throw new InvalidOperationException("Unable to initialize or recover sync database", recreateEx);
            }
        }
        catch
        {
            // Any other failure propagates so the plugin retries on the next start. Release the
            // connection first, or the open handle outlives the failed start.
            CloseConnection();
            throw;
        }
    }

    private SchemaAction OpenSchema(SqliteConnection connection)
        => DatabaseSchema.Open(
            connection,
            DatabaseMigrationService.CurrentSchemaVersion,
            DatabaseMigrationService.MinReaderVersion,
            c => DatabaseMigrationService.CreateInitialSchema((SqliteConnection)c),
            (c, fromVersion) => DatabaseMigrationService.MigrateSchema((SqliteConnection)c, fromVersion, _logger));

    /// <summary>
    /// Closes the current database, moves it aside as a timestamped backup with its journal, and creates a
    /// fresh one. The old file is kept, not deleted: the tracking database carries user intent, Ignored
    /// overrides and pending approvals, that a recovery must not silently destroy. The three newest backups
    /// for each reason are kept.
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
        _logger.LogInformation("Database recreated with fresh schema v{Version}", DatabaseMigrationService.CurrentSchemaVersion);
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
            _logger.LogWarning(ex, "Error disposing old database connection");
        }

        _connection = null;

        try
        {
            _connection = OpenConnection();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
    }

    // ============================================
    // Shared Database Operations
    // ============================================

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

                // Delete main database file with retry logic
                if (File.Exists(_dbPath))
                {
                    DeleteFileWithRetry(_dbPath);
                }

                // Also delete WAL and SHM files if they exist
                DeleteWalFiles();
            });

            InitializeDatabase();
            _logger.LogInformation("Sync database has been reset with fresh schema v{Version}", DatabaseMigrationService.CurrentSchemaVersion);
        }
    }

    // ============================================
    // Dispose Pattern
    // ============================================

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            lock (_writeLock)
            {
                _disposed = true; // Set first inside lock to prevent races

                try
                {
                    DropReaders();
                    _connection?.Close();
                    _connection?.Dispose();
                    _connection = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during database disposal");
                }
            }
        }
    }
}
