using System;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Provides lazy-initialized access to the shared <see cref="SyncDatabase"/> singleton.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ISyncDatabaseProvider))]
public sealed class SyncDatabaseProvider : ISyncDatabaseProvider, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;
    private readonly object _lock = new();
    private readonly IServiceProvider? _services;
    private volatile SyncDatabase? _database;
    private volatile SchemaTooOldException? _tooOld;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncDatabaseProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">Builds the database's logger.</param>
    /// <param name="applicationPaths">Locates Jellyfin's data folder.</param>
    /// <param name="services">Resolves services the database may need, or null.</param>
    public SyncDatabaseProvider(ILoggerFactory loggerFactory, IApplicationPaths applicationPaths, IServiceProvider? services = null)
    {
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
        _services = services;
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

            // A database from a release this one cannot read stays that way until Jellyfin restarts on
            // another release, so it is not opened again on every use.
            if (_tooOld is { } known)
            {
                throw known;
            }

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(SyncDatabaseProvider));
                if (_tooOld is { } found)
                {
                    throw found;
                }

                try
                {
                    _database ??= new SyncDatabase(
                        _loggerFactory.CreateLogger<SyncDatabase>(),
                        _dataPath);
                }
                catch (SchemaTooOldException ex)
                {
                    _tooOld = ex;
                    if (_services?.GetService<JPKribs.Jellyfin.Base.ActivityLogger>() is { } activity)
                    {
                        _ = activity.LogAndWaitAsync("Server Sync Needs an Earlier Update First", "ServerSyncAttention", ex.Message, LogLevel.Error);
                    }

                    throw;
                }

                return _database;
            }
        }
    }

    /// <inheritdoc />
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
