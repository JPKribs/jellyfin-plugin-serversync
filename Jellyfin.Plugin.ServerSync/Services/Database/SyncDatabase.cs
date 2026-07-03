using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
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
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;
    private volatile bool _disposed;

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
    /// Throws if disposed; reopens transparently if closed.
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
    /// Executes a read operation with error handling for transient SQLite errors.
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
        return $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False";
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
            _connection = new SqliteConnection(BuildConnectionString());
            _connection.Open();

            // Set pragmas for reliability in multi-threaded environments
            using (var pragmaCmd = _connection.CreateCommand())
            {
                pragmaCmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA busy_timeout=5000;
                    PRAGMA synchronous=NORMAL;
                ";
                pragmaCmd.ExecuteNonQuery();
            }

            var currentVersion = DatabaseMigrationService.GetSchemaVersion(_connection);

            if (currentVersion == 0)
            {
                DatabaseMigrationService.CreateInitialSchema(_connection);
                DatabaseMigrationService.SetSchemaVersion(_connection, DatabaseMigrationService.CurrentSchemaVersion);
            }
            else if (currentVersion < DatabaseMigrationService.CurrentSchemaVersion)
            {
                var migrationSucceeded = DatabaseMigrationService.MigrateSchema(_connection, currentVersion, _logger);
                if (!migrationSucceeded)
                {
                    _logger.LogWarning("Migration failed, recreating database with fresh schema");
                    RecreateDatabase();
                    return;
                }
            }

            _logger.LogDebug("Sync database initialized at {DbPath} (schema v{Version})", _dbPath, DatabaseMigrationService.CurrentSchemaVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database, attempting recovery");
            try
            {
                RecreateDatabase();
            }
            catch (Exception recreateEx)
            {
                _logger.LogError(recreateEx, "Failed to recreate database");
                throw new InvalidOperationException("Unable to initialize or recover sync database", recreateEx);
            }
        }
    }

    /// <summary>
    /// Closes the current database, moves it aside as a timestamped backup,
    /// and creates a fresh one. The old file is preserved (not deleted): the
    /// tracking DB carries user intent — Ignored overrides and pending
    /// deletion/download approvals — that a transient init failure (disk
    /// briefly full, permissions hiccup at boot) must not silently destroy.
    /// </summary>
    private void RecreateDatabase()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;

        if (File.Exists(_dbPath))
        {
            var backupPath = _dbPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                File.Move(_dbPath, backupPath);
                _logger.LogWarning("Moved unreadable database aside to {BackupPath}; a fresh database will be created", backupPath);
                PruneOldCorruptBackups();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to back up unreadable database file; deleting it instead");
                try
                {
                    File.Delete(_dbPath);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx, "Failed to delete unreadable database file, attempting to overwrite");
                }
            }
        }

        // Also delete WAL and SHM files if they exist
        DeleteWalFiles();

        _connection = new SqliteConnection(BuildConnectionString());
        _connection.Open();

        // Set pragmas on fresh connection
        using (var pragmaCmd = _connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();
        }

        DatabaseMigrationService.CreateInitialSchema(_connection);
        DatabaseMigrationService.SetSchemaVersion(_connection, DatabaseMigrationService.CurrentSchemaVersion);
        _logger.LogInformation("Database recreated with fresh schema v{Version}", DatabaseMigrationService.CurrentSchemaVersion);
    }

    /// <summary>
    /// Keeps only the three most recent <c>.corrupt-*</c> backups so repeated
    /// recovery attempts can't fill the disk.
    /// </summary>
    private void PruneOldCorruptBackups()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var backups = Directory.GetFiles(dir, Path.GetFileName(_dbPath) + ".corrupt-*");
            if (backups.Length <= 3)
            {
                return;
            }

            Array.Sort(backups, StringComparer.Ordinal);
            for (var i = 0; i < backups.Length - 3; i++)
            {
                File.Delete(backups[i]);
                _logger.LogDebug("Pruned old corrupt-database backup {Path}", backups[i]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to prune old corrupt-database backups");
        }
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

        SqliteConnection? newConnection = null;
        try
        {
            newConnection = new SqliteConnection(BuildConnectionString());
            newConnection.Open();

            // Re-apply pragmas on reconnection
            using var pragmaCmd = newConnection.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();

            _connection = newConnection;
            newConnection = null; // Transfer ownership, prevent dispose in finally
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
        finally
        {
            // If newConnection is still set, we failed after Open() but before
            // assigning to _connection — dispose to prevent leak
            newConnection?.Dispose();
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

            _connection?.Close();
            _connection?.Dispose();
            _connection = null;

            // Delete main database file with retry logic
            if (File.Exists(_dbPath))
            {
                DeleteFileWithRetry(_dbPath);
            }

            // Also delete WAL and SHM files if they exist
            DeleteWalFiles();

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
