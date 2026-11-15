using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A real content sync table over a sync database in a temp folder, with a media folder beside it for
/// the links a test writes. Disposing it deletes the folder.
/// </summary>
internal sealed class LinkTestTable : IDisposable
{
    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkTestTable"/> class.
    /// </summary>
    public LinkTestTable()
    {
        Folder = Path.Combine(Path.GetTempPath(), "serversync-linktable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(MediaRoot);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, Folder);
        Table = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
    }

    /// <summary>
    /// Gets the temp folder that holds the database and the link key.
    /// </summary>
    public string Folder { get; }

    /// <summary>
    /// Gets the folder that stands in for a mapped library.
    /// </summary>
    public string MediaRoot => Path.Combine(Folder, "media");

    /// <summary>
    /// Gets the content sync table.
    /// </summary>
    public ContentSyncTableManager Table { get; }

    /// <summary>
    /// The path of a file inside the media folder.
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <returns>The full path.</returns>
    public string Media(string name) => Path.Combine(MediaRoot, name);

    /// <summary>
    /// Builds a row for a file the source holds.
    /// </summary>
    /// <param name="id">The source's id of the file.</param>
    /// <param name="localPath">Where the link or the copy sits here.</param>
    /// <param name="mode">How the file is stored.</param>
    /// <param name="status">The row's status.</param>
    /// <param name="sourcePath">The file's path on the source.</param>
    /// <param name="serverKey">The key of the server entry the row belongs to.</param>
    /// <returns>The row, not yet saved.</returns>
    public static SyncItem Row(string id, string? localPath, StorageMode mode = StorageMode.Link, SyncStatus status = SyncStatus.Synced, string sourcePath = "/src/Movie.mkv", string serverKey = "server")
        => new()
        {
            SourceItemId = id,
            SourceLibraryId = "s",
            LocalLibraryId = "l",
            SourcePath = sourcePath,
            SourceSize = 1,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = localPath,
            StorageMode = mode,
            Status = status,
            StatusDate = DateTime.UtcNow,
            ServerKey = serverKey
        };

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
