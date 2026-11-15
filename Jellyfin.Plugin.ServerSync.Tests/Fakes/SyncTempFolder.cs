using System;
using System.IO;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A folder under the system temp path that a test fills with real files, removed with everything in it on dispose.
/// </summary>
public sealed class SyncTempFolder : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTempFolder"/> class and creates the folder.
    /// </summary>
    /// <param name="label">A short word naming the test class, so a leftover folder can be traced.</param>
    public SyncTempFolder(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), "serversync-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Gets the folder's full path.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Builds a path inside the folder without creating anything.
    /// </summary>
    /// <param name="parts">The folder and file names below the root.</param>
    /// <returns>The full path.</returns>
    public string PathOf(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    /// <summary>
    /// Writes a file inside the folder, creating its parent folders.
    /// </summary>
    /// <param name="relativePath">The file's path below the root.</param>
    /// <param name="content">The text the file holds.</param>
    /// <returns>The file's full path.</returns>
    public string Write(string relativePath, string content = "x")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Creates a folder inside the folder.
    /// </summary>
    /// <param name="relativePath">The folder's path below the root.</param>
    /// <returns>The folder's full path.</returns>
    public string Folder(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
