namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>Helpers for <see cref="StorageMode"/>.</summary>
public static class StorageModes
{
    /// <summary>Whether the file here is a .strm link, including one on its way to a copy.</summary>
    /// <param name="mode">The Storage of the file to test.</param>
    /// <returns><c>true</c> when the mode is Link or Promoting.</returns>
    public static bool IsLink(this StorageMode mode) => mode is StorageMode.Link or StorageMode.Promoting;
}
