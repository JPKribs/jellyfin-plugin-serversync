using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A real sync database in a temp folder, opened through the schema and deleted when disposed.
/// </summary>
public sealed class QueueTestDatabase : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QueueTestDatabase"/> class.
    /// </summary>
    public QueueTestDatabase()
    {
        Folder = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, Folder);
        Provider = new FixedProvider(Database);
    }

    /// <summary>Gets the temp folder that holds the database file.</summary>
    public string Folder { get; }

    /// <summary>Gets the open database.</summary>
    public SyncDatabase Database { get; }

    /// <summary>Gets the provider that hands every store this database.</summary>
    public FixedProvider Provider { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Database.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
