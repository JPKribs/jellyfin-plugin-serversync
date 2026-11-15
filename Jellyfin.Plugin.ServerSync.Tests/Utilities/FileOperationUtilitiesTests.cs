using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// File moves land whole, and the files found beside a media file are that file's own and never a neighbor's.
/// </summary>
public sealed class FileOperationUtilitiesTests : IDisposable
{
    private static readonly DateTime At = new(2026, 1, 29, 17, 30, 45, DateTimeKind.Utc);

    private readonly SyncTempFolder _temp = new("fileops");

    /// <inheritdoc/>
    public void Dispose() => _temp.Dispose();

    private static string[] Names(System.Collections.Generic.IEnumerable<string> paths)
        => paths.Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The images Jellyfin names after a media file are found, and other files that start with its name are not.
    /// True: removing a film removes its poster and fanart and nothing else.
    /// False: a note or another film's art would be deleted along with the film.
    /// </summary>
    [Fact]
    public void GetItemArtifacts_ImagesNamedAfterTheFile_AreFound()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film-poster.jpg");
        _temp.Write("Film-fanart2.png");
        _temp.Write("Film-thumb.webp");
        _temp.Write("Film-notes.jpg");
        _temp.Write("Film-poster.txt");
        _temp.Write("Film-poster-old.jpg");

        var (images, _) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Equal(new[] { "Film-fanart2.png", "Film-poster.jpg", "Film-thumb.webp" }, Names(images));
    }

    /// <summary>
    /// The art of another title whose name starts with this one's is left alone.
    /// True: removing "Film" keeps the poster of "Film-Sequel" and of "Film 2".
    /// False: a title that shares a name prefix would lose its images.
    /// </summary>
    [Fact]
    public void GetItemArtifacts_TitleSharingThePrefix_KeepsItsImages()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film-Sequel.mkv");
        _temp.Write("Film-Sequel-poster.jpg");
        _temp.Write("Film 2.mkv");
        _temp.Write("Film 2-poster.jpg");
        _temp.Write("Film2-poster.jpg");

        var (images, _) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Empty(images);
    }

    /// <summary>
    /// The trickplay folder named after the file is returned when it exists, and null when it does not.
    /// True: removing a film removes its own trickplay tiles.
    /// False: tiles would be left behind, or a missing folder would be handed to a delete.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetItemArtifacts_TrickplayFolder_IsReturnedOnlyWhenPresent(bool present)
    {
        var main = _temp.Write("Film.mkv");
        _temp.Folder("Film 2.trickplay");
        var expected = present ? _temp.Folder("Film.trickplay") : null;

        var (_, trickplay) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Equal(expected, trickplay);
    }

    /// <summary>
    /// When another media file has the same name, nothing is matched, not even the trickplay folder.
    /// True: removing one container of a title keeps the art the other container still uses.
    /// False: deleting "Film.mkv" would strip the poster from "Film.mp4".
    /// </summary>
    [Fact]
    public void GetItemArtifacts_SecondContainerOfTheTitle_MatchesNothing()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film.mp4");
        _temp.Write("Film-poster.jpg");
        _temp.Folder("Film.trickplay");

        var (images, trickplay) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Empty(images);
        Assert.Null(trickplay);
    }

    /// <summary>
    /// A subtitle, an NFO, or a file being replaced beside the media file does not count as a second container.
    /// True: a film with subtitles still has its images found.
    /// False: any film with a subtitle would keep its art forever after removal.
    /// </summary>
    [Fact]
    public void GetItemArtifacts_CompanionsBesideTheFile_DoNotHideTheImages()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film.srt");
        _temp.Write("Film.nfo");
        _temp.Write("Film.replacing.mkv");
        _temp.Write("Film-poster.jpg");

        var (images, _) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Equal(new[] { "Film-poster.jpg" }, Names(images));
    }

    /// <summary>
    /// A file in a folder that does not exist, or a bare name with no folder, matches nothing.
    /// True: a row whose file is already gone cleans up quietly.
    /// False: a missing folder would throw and fail the whole deletion.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetItemArtifacts_NoFolderToRead_MatchesNothing(bool missingFolder)
    {
        var main = missingFolder ? _temp.PathOf("gone", "Film.mkv") : "Film.mkv";

        var (images, trickplay) = FileOperationUtilities.GetItemArtifacts(main);

        Assert.Empty(images);
        Assert.Null(trickplay);
    }

    /// <summary>
    /// A move puts the file's content at the destination and removes the source.
    /// True: a finished download becomes the library file.
    /// False: the file would be copied and left behind, or not arrive.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_NewDestination_MovesTheFile()
    {
        var source = _temp.Write("temp/film.tmp", "new bytes");
        var destination = _temp.PathOf("temp", "film.mkv");

        await FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0);

        Assert.False(File.Exists(source));
        Assert.Equal("new bytes", File.ReadAllText(destination));
    }

    /// <summary>
    /// A move over an existing file replaces its content.
    /// True: a replacement takes the place of the old file.
    /// False: a replacement would fail on every title that already has a file.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_DestinationExists_ReplacesIt()
    {
        var source = _temp.Write("film.tmp", "new bytes");
        var destination = _temp.Write("film.mkv", "old bytes");

        await FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0);

        Assert.False(File.Exists(source));
        Assert.Equal("new bytes", File.ReadAllText(destination));
    }

    /// <summary>
    /// A move leaves a sibling whose name shares the destination's prefix untouched.
    /// True: replacing "film.mkv" leaves "film.mkv2" as it was.
    /// False: a move would damage a neighbor with a similar name.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_SiblingSharingThePrefix_IsUntouched()
    {
        var source = _temp.Write("film.tmp", "new bytes");
        var destination = _temp.PathOf("film.mkv");
        var sibling = _temp.Write("film.mkv2", "sibling bytes");

        await FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0);

        Assert.Equal("sibling bytes", File.ReadAllText(sibling));
        Assert.Equal("new bytes", File.ReadAllText(destination));
    }

    /// <summary>
    /// A move whose source is missing fails after its attempts, and an existing destination keeps its content.
    /// True: a lost temp file surfaces as an error and the library file survives.
    /// False: the old file would be removed with nothing to take its place.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MoveFileWithOverwriteAsync_SourceMissing_ThrowsAndKeepsTheDestination(int maxRetries)
    {
        var source = _temp.PathOf("missing.tmp");
        var destination = _temp.Write("film.mkv", "old bytes");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, maxRetries: maxRetries, retryDelayMs: 0));

        Assert.Equal("old bytes", File.ReadAllText(destination));
    }

    /// <summary>
    /// A move into a folder that does not exist fails and leaves the source where it was.
    /// True: a download whose target folder vanished is kept for the next try.
    /// False: the downloaded file would be lost.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_DestinationFolderMissing_ThrowsAndKeepsTheSource()
    {
        var source = _temp.Write("film.tmp", "new bytes");
        var destination = _temp.PathOf("gone", "film.mkv");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0));

        Assert.Equal("new bytes", File.ReadAllText(source));
    }

    /// <summary>
    /// A cancelled token ends the wait between attempts with a cancellation.
    /// True: a stopped task does not sit through its retries.
    /// False: cancelling a run would still wait out every retry of a failing move.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_CancelledWhileRetrying_ThrowsCancellation()
    {
        var source = _temp.PathOf("missing.tmp");
        var destination = _temp.PathOf("film.mkv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0, cancellationToken: cts.Token));

        Assert.False(File.Exists(destination));
    }

    /// <summary>
    /// A cancelled token does not stop a move that works on its first attempt.
    /// True: a file that is ready is put in place even as the run winds down.
    /// False: a finished download would be stranded as a temp file on cancel.
    /// </summary>
    [Fact]
    public async Task MoveFileWithOverwriteAsync_CancelledButMoveWorks_StillMoves()
    {
        var source = _temp.Write("film.tmp", "new bytes");
        var destination = _temp.PathOf("film.mkv");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FileOperationUtilities.MoveFileWithOverwriteAsync(source, destination, retryDelayMs: 0, cancellationToken: cts.Token);

        Assert.Equal("new bytes", File.ReadAllText(destination));
    }

    /// <summary>
    /// A recycled name is the folder path joined with dots, the file name, the time, and the extension.
    /// True: a recycled file says where it came from and when.
    /// False: two files of the same name from different folders would collide in the bin.
    /// </summary>
    [Theory]
    [InlineData("/media/Shows/Show/Season 01/E01.mkv", "media.Shows.Show.Season 01.E01_2026-01-29_17-30-45.mkv")]
    [InlineData("/media/a:b/Title.mkv", "media.a.b.Title_2026-01-29_17-30-45.mkv")]
    [InlineData("/media/README", "media.README_2026-01-29_17-30-45")]
    [InlineData("/Title.mkv", "Title_2026-01-29_17-30-45.mkv")]
    [InlineData("Title.mkv", "Title_2026-01-29_17-30-45.mkv")]
    [InlineData("/media/Title.en.srt", "media.Title.en_2026-01-29_17-30-45.srt")]
    public void GenerateRecycledFileName_ShortPath_EncodesFolderNameAndTime(string original, string expected)
    {
        var name = FileOperationUtilities.GenerateRecycledFileName(original, At);

        Assert.Equal(expected, name);
    }

    /// <summary>
    /// The name built without a time is one the retention cleanup can read back.
    /// True: every file the bin takes can later expire.
    /// False: files recycled through this overload would never be cleaned up.
    /// </summary>
    [Fact]
    public void GenerateRecycledFileName_WithoutATime_BuildsAReadableName()
    {
        var name = FileOperationUtilities.GenerateRecycledFileName("/media/Movies/Title.mkv");

        Assert.StartsWith("media.Movies.Title_", name, StringComparison.Ordinal);
        Assert.EndsWith(".mkv", name, StringComparison.Ordinal);
        Assert.True(FileOperationUtilities.TryReadRecycledTime(name, out _));
    }

    /// <summary>
    /// A name the bin builds gives back its time in UTC, with or without a suffix, an extension, or a folder in front.
    /// True: retention measures a recycled file's age from when it was recycled.
    /// False: the time would shift by the server's zone and files would expire hours early or late.
    /// </summary>
    [Theory]
    [InlineData("media.Movies.Title_2026-01-29_17-30-45.mkv")]
    [InlineData("media.Movies.Title_2026-01-29_17-30-45_0a1b2c3d.mkv")]
    [InlineData("media.Movies.Title_2026-01-29_17-30-45")]
    [InlineData("/bin/sub/media.Movies.Title_2026-01-29_17-30-45.mkv")]
    [InlineData("a_2026-01-29_17-30-45.")]
    public void TryReadRecycledTime_NameTheBinBuilds_ReadsTheUtcTime(string path)
    {
        var read = FileOperationUtilities.TryReadRecycledTime(path, out var recycledAt);

        Assert.True(read);
        Assert.Equal(At, recycledAt);
        Assert.Equal(DateTimeKind.Utc, recycledAt.Kind);
    }

    /// <summary>
    /// A name the bin would not build is not read as a recycled file.
    /// True: a stranger's file in the bin folder is never aged and deleted.
    /// False: any file with a date in its name could be deleted by retention.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Title.mkv")]
    [InlineData("_2026-01-29_17-30-45.mkv")]
    [InlineData("Title_2026-13-45_99-99-99.mkv")]
    [InlineData("Title_2026-01-29_17-30-45_0A1B2C3D.mkv")]
    [InlineData("Title_2026-01-29_17-30-45_0a1b2c3.mkv")]
    [InlineData("Title_2026-01-29_17-30-45.tar.gz")]
    [InlineData("Title_2026-01-29.mkv")]
    [InlineData("Title 2026-01-29_17-30-45.mkv")]
    public void TryReadRecycledTime_OtherNames_AreRefused(string? path)
    {
        var read = FileOperationUtilities.TryReadRecycledTime(path!, out var recycledAt);

        Assert.False(read);
        Assert.Equal(default, recycledAt);
    }

    /// <summary>
    /// The time is returned for a recycled name and null for any other.
    /// True: a caller can tell a recycled file from a stranger's with one call.
    /// False: a stranger's file would come back with the default date and look ancient.
    /// </summary>
    [Fact]
    public void ExtractTimestampFromFileName_RecycledOrNot_ReturnsTimeOrNull()
    {
        var recycled = FileOperationUtilities.ExtractTimestampFromFileName("media.Title_2026-01-29_17-30-45.mkv");
        var other = FileOperationUtilities.ExtractTimestampFromFileName("notes.txt");

        Assert.Equal(At, recycled);
        Assert.Null(other);
    }

    /// <summary>
    /// The subtitles, NFO, and image that share the media file's name are its companions, and the file itself is not.
    /// True: moving or removing a film takes its subtitles and NFO with it.
    /// False: orphan subtitles would be left behind, or the film itself would be listed twice.
    /// </summary>
    [Fact]
    public void GetCompanionFiles_FilesSharingTheName_AreFound()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film.srt");
        _temp.Write("Film.en.forced.ass");
        _temp.Write("Film.nfo");
        _temp.Write("Film.jpg");

        var companions = FileOperationUtilities.GetCompanionFiles(main);

        Assert.Equal(new[] { "Film.en.forced.ass", "Film.jpg", "Film.nfo", "Film.srt" }, Names(companions));
    }

    /// <summary>
    /// Files of a title that only shares the name's start are not companions.
    /// True: removing "Film" keeps the subtitles of "Film 2" and "Film2".
    /// False: a neighbor with a similar name would lose its subtitles.
    /// </summary>
    [Fact]
    public void GetCompanionFiles_NeighborSharingThePrefix_IsNotACompanion()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film 2.mkv");
        _temp.Write("Film 2.srt");
        _temp.Write("Film2.srt");
        _temp.Write("Film-poster.jpg");

        var companions = FileOperationUtilities.GetCompanionFiles(main);

        Assert.Empty(companions);
    }

    /// <summary>
    /// When another media file has the same name, its companions stay with it and none is returned.
    /// True: removing one container of a title keeps the subtitles the other still uses.
    /// False: deleting "Film.mkv" would take the subtitles of "Film.mp4" with it.
    /// </summary>
    [Fact]
    public void GetCompanionFiles_SecondContainerOfTheTitle_ReturnsNone()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film.mp4");
        _temp.Write("Film.srt");

        var companions = FileOperationUtilities.GetCompanionFiles(main);

        Assert.Empty(companions);
    }

    /// <summary>
    /// A file being replaced beside the media file does not take the companions away.
    /// True: a replacement in progress still carries its subtitles across.
    /// False: every replacement would orphan the title's subtitles.
    /// </summary>
    [Fact]
    public void GetCompanionFiles_ReplacementInProgress_StillReturnsCompanions()
    {
        var main = _temp.Write("Film.mkv");
        _temp.Write("Film.replacing.mkv");
        _temp.Write("Film.srt");

        var companions = FileOperationUtilities.GetCompanionFiles(main);

        Assert.Equal(new[] { "Film.srt" }, Names(companions));
    }

    /// <summary>
    /// A media file in a folder that does not exist has no companions.
    /// True: a row whose folder is gone cleans up quietly.
    /// False: a missing folder would throw and fail the whole run.
    /// </summary>
    [Fact]
    public void GetCompanionFiles_FolderMissing_ReturnsNone()
    {
        var companions = FileOperationUtilities.GetCompanionFiles(_temp.PathOf("gone", "Film.mkv"));

        Assert.Empty(companions);
    }

    /// <summary>
    /// An existing folder that can be written passes the probe and holds no probe file afterward.
    /// True: the settings page accepts a usable folder and leaves it clean.
    /// False: every probe would litter the library with a hidden file.
    /// </summary>
    [Fact]
    public void HasWritePermission_WritableFolder_IsTrueAndLeavesNothingBehind()
    {
        var folder = _temp.Folder("library");

        var writable = FileOperationUtilities.HasWritePermission(folder);

        Assert.True(writable);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    /// <summary>
    /// A folder that does not exist is created and passes.
    /// True: a new download folder is made ready by the probe.
    /// False: a first run into a new folder would be refused.
    /// </summary>
    [Fact]
    public void HasWritePermission_MissingFolder_CreatesIt()
    {
        var folder = _temp.PathOf("new", "nested");

        var writable = FileOperationUtilities.HasWritePermission(folder);

        Assert.True(writable);
        Assert.True(Directory.Exists(folder));
    }

    /// <summary>
    /// No path, an empty path, and a path that runs through a file all fail the probe.
    /// True: an unusable folder is reported before a download is tried.
    /// False: the probe would throw, or a download would start into a place it cannot write.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HasWritePermission_UnusablePath_IsFalse(int scenario)
    {
        var path = scenario switch
        {
            0 => null,
            1 => string.Empty,
            _ => Path.Combine(_temp.Write("a-file.txt"), "below")
        };

        var writable = FileOperationUtilities.HasWritePermission(path!);

        Assert.False(writable);
    }
}
