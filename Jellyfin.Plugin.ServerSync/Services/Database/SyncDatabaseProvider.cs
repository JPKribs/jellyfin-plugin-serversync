using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Provides lazy-initialized access to the shared <see cref="SyncDatabase"/> singleton.
/// </summary>
public sealed class SyncDatabaseProvider : ISyncDatabaseProvider, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;
    private readonly object _lock = new();
    private volatile SyncDatabase? _database;
    private volatile bool _disposed;

    public SyncDatabaseProvider(ILoggerFactory loggerFactory, IApplicationPaths applicationPaths)
    {
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
    }

    /// <inheritdoc />
    public SyncDatabase Database
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SyncDatabaseProvider));

            if (_database != null)
            {
                return _database;
            }

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(SyncDatabaseProvider));

                _database ??= new SyncDatabase(
                    _loggerFactory.CreateLogger<SyncDatabase>(),
                    _dataPath);
                return _database;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _database?.Dispose();
                _disposed = true;
            }
        }
    }
}
