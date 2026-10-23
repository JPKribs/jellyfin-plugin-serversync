using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Retention cleanup of the recycling bin, which may be a folder shared with other tools. The only
/// folders it may delete are the recycled trickplay folders the plugin itself names, once expired.
/// </summary>
public sealed class RecyclingBinRetentionTests : IDisposable
{
    private readonly string _bin;

    public RecyclingBinRetentionTests()
    {
        _bin = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_bin);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_bin, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_bin, name);
        Directory.CreateDirectory(Path.Combine(path, "320 - 10x10"));
        File.WriteAllText(Path.Combine(path, "320 - 10x10", "0.jpg"), "12345");
        return path;
    }

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The plugin's own expired trickplay folders go, with and without the collision suffix, and one
    /// recycled recently stays.
    /// True: recycled trickplay is cleared on the same schedule as recycled files.
    /// False: trickplay piles up in the bin, or is deleted before its retention ends.
    /// </summary>
    [Fact]
    public void Cleanup_RemovesOnlyExpiredRecycledTrickplay()
    {
        var expired = Folder("media.Movies.Title (2020).Title (2020)_2020-01-01_00-00-00.trickplay");
        var expiredSuffixed = Folder("media.Movies.Title (2020).Title (2020)_2020-01-01_00-00-00.trickplay_0a1b2c3d");
        var recent = Folder("media.Movies.New.New_" + Stamp(DateTime.UtcNow.AddDays(-1)) + ".trickplay");

        var deleted = RecyclingBinService.CleanupExpiredFiles(_bin, 30, NullLogger.Instance);

        Assert.Equal(2, deleted);
        Assert.False(Directory.Exists(expired));
        Assert.False(Directory.Exists(expiredSuffixed));
        Assert.True(Directory.Exists(recent));
    }

    /// <summary>
    /// Folders the plugin did not create are never touched, even old ones named with a timestamp.
    /// True: a bin shared with backups or other tools keeps their folders.
    /// False: a folder like Backup_2024-01-01_10-00-00 is deleted as if it were recycled trickplay.
    /// </summary>
    [Theory]
    [InlineData("Backup_2024-01-01_10-00-00")]
    [InlineData("Photos_2020-01-01_00-00-00.zipdir")]
    [InlineData("Movie_2020-01-01_00-00-00.trickplay_notahexsuffix")]
    [InlineData("_2020-01-01_00-00-00.trickplay")]
    [InlineData("old-folder")]
    public void Cleanup_LeavesFoldersItDidNotCreate(string name)
    {
        var folder = Folder(name);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddYears(-3));

        RecyclingBinService.CleanupExpiredFiles(_bin, 30, NullLogger.Instance);

        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(folder, "320 - 10x10", "0.jpg")));
    }

    private string FileIn(string name, DateTime? modified = null)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, "12345");
        File.SetLastWriteTimeUtc(path, modified ?? DateTime.UtcNow);
        return path;
    }

    /// <summary>
    /// The plugin's expired files go, including ones renamed because their name was taken, and recent
    /// ones stay.
    /// True: recycled files expire on schedule.
    /// False: renamed duplicates never parse and pile up, or recent files go early.
    /// </summary>
    [Fact]
    public void Cleanup_RemovesOnlyExpiredRecycledFiles()
    {
        var old = DateTime.UtcNow.AddDays(-60);
        var expired = FileIn($"media.Movies.Title_{Stamp(old)}.mkv");
        var expiredSuffixed = FileIn($"media.Movies.Title_{Stamp(old)}_0a1b2c3d.mkv");
        var recent = FileIn($"media.Movies.Title_{Stamp(DateTime.UtcNow)}.mkv");

        var deleted = RecyclingBinService.CleanupExpiredFiles(_bin, 30, NullLogger.Instance);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(expiredSuffixed));
        Assert.True(File.Exists(recent));
    }

    /// <summary>
    /// Files the plugin did not name are never touched, however old.
    /// True: a bin shared with other tools keeps their files.
    /// False: any old file in the folder is deleted by its modified time.
    /// </summary>
    [Theory]
    [InlineData("notes.txt")]
    [InlineData("backup.tar.gz")]
    [InlineData("Backup_2024-01-01.zip")]
    public void Cleanup_LeavesFilesItDidNotName(string name)
    {
        var file = FileIn(name, DateTime.UtcNow.AddYears(-3));

        RecyclingBinService.CleanupExpiredFiles(_bin, 30, NullLogger.Instance);

        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// A file deep in a library gets a recycled name the filesystem accepts, that still carries its
    /// timestamp and extension, and two deep files in different folders keep different names.
    /// True: recycling works for long TV paths.
    /// False: the move fails with a name too long, and the deletion or replacement errors.
    /// </summary>
    [Fact]
    public void RecycledName_ForADeepPath_FitsTheFilesystemLimit()
    {
        var deep = string.Join("/", new string('a', 120), new string('b', 120), new string('c', 120));
        var at = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        var one = FileOperationUtilities.GenerateRecycledFileName($"/media/{deep}/Show/Season 01/Show - S01E01 - Pilot.mkv", at);
        var two = FileOperationUtilities.GenerateRecycledFileName($"/other/{deep}/Show/Season 01/Show - S01E01 - Pilot.mkv", at);

        Assert.True(Encoding.UTF8.GetByteCount(one) <= FileOperationUtilities.MaxRecycledNameBytes);
        Assert.EndsWith("_2026-10-23_12-00-00.mkv", one, StringComparison.Ordinal);
        Assert.Contains("Show - S01E01 - Pilot", one, StringComparison.Ordinal);
        Assert.NotEqual(one, two);
        Assert.True(FileOperationUtilities.TryReadRecycledTime(one, out var read));
        Assert.Equal(at, read);
    }

    /// <summary>
    /// A file whose own name is near the limit still gets a name that fits, cut without splitting a
    /// multi-byte character.
    /// </summary>
    [Fact]
    public void RecycledName_ForAVeryLongFileName_IsCutToFit()
    {
        var name = new string('é', 200) + ".mkv";

        var recycled = FileOperationUtilities.GenerateRecycledFileName("/media/Movies/" + name, DateTime.UtcNow);

        Assert.True(Encoding.UTF8.GetByteCount(recycled) <= FileOperationUtilities.MaxRecycledNameBytes);
        Assert.EndsWith(".mkv", recycled, StringComparison.Ordinal);
        Assert.True(FileOperationUtilities.TryReadRecycledTime(recycled, out _));
    }

    /// <summary>A short path is named exactly as before.</summary>
    [Fact]
    public void RecycledName_ForAShortPath_IsUnchanged()
    {
        var at = new DateTime(2026, 1, 29, 17, 30, 45, DateTimeKind.Utc);

        var recycled = FileOperationUtilities.GenerateRecycledFileName(Path.Combine("/media", "Movies", "Title.mkv"), at);

        Assert.Equal("media.Movies.Title_2026-01-29_17-30-45.mkv", recycled);
    }
}
