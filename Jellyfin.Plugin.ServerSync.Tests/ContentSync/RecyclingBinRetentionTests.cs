using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services;
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
}
