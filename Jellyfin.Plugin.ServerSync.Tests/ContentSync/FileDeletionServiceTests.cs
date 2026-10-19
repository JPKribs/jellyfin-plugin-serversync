using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Deleting a file from the Sync tab. Jellyfin's own file deletion removes the whole containing
/// folder, recursively, for a video that is not in a mixed folder (Video.GetDeletePaths answers
/// ContainingFolderPath for it), so the plugin deletes the file and its companions itself and asks
/// Jellyfin only to forget the item.
/// </summary>
public sealed class FileDeletionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _root;
    private readonly string _movieDir;

    public FileDeletionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "Movies");
        _movieDir = Path.Combine(_root, "Title (2020)");
        Directory.CreateDirectory(_movieDir);
    }

    // A configuration whose one mapping makes the temp Movies folder a library root.
    private PluginConfiguration Config(bool removeEmptyFolders = false)
    {
        var config = new PluginConfiguration { RemoveEmptyFoldersOnDelete = removeEmptyFolders };
        var server = new SourceServer { Name = "Source", Url = "http://source:8096", ApiKey = "k", Mode = ServerMode.Pull, IsEnabled = true };
        server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib-1", LocalLibraryId = "local-1", LocalRootPath = _root, IsEnabled = true });
        config.Servers.Add(server);
        return config;
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_movieDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A movie in its own folder is deleted with its companions, the other files in the folder stay,
    /// and Jellyfin is asked to remove the item without deleting anything on disk.
    /// True: deleting one version or one file never takes the rest of the folder with it.
    /// False: Jellyfin deletes the movie's folder recursively, extras and all.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_LibraryItem_DeletesOnlyTheFileAndCompanions_AndKeepsJellyfinOffTheDisk()
    {
        var moviePath = Path.Combine(_movieDir, "Title (2020).mkv");
        File.WriteAllText(moviePath, "movie");
        File.WriteAllText(Path.Combine(_movieDir, "Title (2020).nfo"), "<movie/>");
        File.WriteAllText(Path.Combine(_movieDir, "Title (2020).en.srt"), "1");
        var otherVersion = Path.Combine(_movieDir, "Title (2020) - 4K.mkv");
        File.WriteAllText(otherVersion, "other version");
        var extra = Path.Combine(_movieDir, "notes.txt");
        File.WriteAllText(extra, "keep me");
        var thumb = Write("Title (2020)-thumb.jpg");
        var trickplay = Write(Path.Combine("Title (2020).trickplay", "320 - 10x10", "0.jpg"));
        var otherThumb = Write("Title (2020) - 4K-thumb.jpg");
        var otherTrickplay = Write(Path.Combine("Title (2020) - 4K.trickplay", "320 - 10x10", "0.jpg"));
        var poster = Write("poster.jpg");
        var nfo = Write("movie.nfo");

        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();
        var movie = new Movie { Name = "Title", Path = moviePath };

        var result = FileDeletionService.DeleteLocalFile(moviePath, movie, libraryManager, Config(removeEmptyFolders: true), NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(moviePath));
        Assert.False(File.Exists(Path.Combine(_movieDir, "Title (2020).nfo")));
        Assert.False(File.Exists(Path.Combine(_movieDir, "Title (2020).en.srt")));
        Assert.True(File.Exists(otherVersion));
        Assert.True(File.Exists(extra));
        Assert.False(File.Exists(thumb));
        Assert.False(Directory.Exists(Path.Combine(_movieDir, "Title (2020).trickplay")));
        Assert.True(File.Exists(otherThumb));
        Assert.True(File.Exists(otherTrickplay));
        Assert.False(File.Exists(trickplay));

        // The other version still lives here, so the title's own artwork stays.
        Assert.True(File.Exists(poster));
        Assert.True(File.Exists(nfo));

        var delete = Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
        var options = Assert.IsType<DeleteOptions>(delete.Args.Single(a => a is DeleteOptions));
        Assert.False(options.DeleteFileLocation);
        Assert.Same(movie, delete.Args[0]);
    }

    /// <summary>
    /// A movie that had its folder to itself leaves nothing behind: its companions, its own images and
    /// trickplay, the title artwork and nfo, and, with empty folders removed, the folder itself. The
    /// library root stays.
    /// True: deleting from the Sync tab cleans up as fully as Jellyfin's own delete did.
    /// False: posters, nfo files, and empty folders pile up under the library.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_OnlyItemInItsFolder_RemovesTheWholeTitle()
    {
        var moviePath = Write("Title (2020).mkv", "movie");
        Write("Title (2020).nfo");
        Write("Title (2020)-thumb.jpg");
        Write(Path.Combine("Title (2020).trickplay", "320 - 10x10", "0.jpg"));
        Write("poster.jpg");
        Write("fanart.jpg");
        Write("backdrop1.png");
        Write("movie.nfo");
        var rootPoster = Path.Combine(_root, "folder.jpg");
        File.WriteAllText(rootPoster, "library art");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(moviePath, new Movie { Name = "Title", Path = moviePath }, libraryManager, Config(removeEmptyFolders: true), NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Directory.Exists(_movieDir));
        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(rootPoster));
        Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
    }

    /// <summary>
    /// With Remove empty folders off, only the item's own files go: the file, its same name
    /// companions, its thumbnail, and its trickplay. The title's artwork and nfo stay with the folder.
    /// True: the setting decides about everything beyond the item itself.
    /// False: title artwork is deleted from a folder the user chose to keep.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_OnlyItem_EmptyFoldersOff_DeletesOnlyTheItemsOwnFiles()
    {
        var moviePath = Write("Title (2020).mkv", "movie");
        Write("Title (2020).nfo");
        Write("Title (2020)-thumb.jpg");
        Write(Path.Combine("Title (2020).trickplay", "320 - 10x10", "0.jpg"));
        var poster = Write("poster.jpg");
        var nfo = Write("movie.nfo");
        var (libraryManager, _) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(moviePath, new Movie { Name = "Title", Path = moviePath }, libraryManager, Config(removeEmptyFolders: false), NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { nfo, poster }, Directory.GetFileSystemEntries(_movieDir).OrderBy(e => e, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The same folder with Remove empty folders on: the title files go and the folder with them.
    /// True: switching the setting on is what removes a title's leftovers.
    /// False: a folder holding only a poster and an nfo stays behind.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_OnlyItem_EmptyFoldersOn_RemovesTheFolder()
    {
        var moviePath = Write("Title (2020).mkv", "movie");
        Write("poster.jpg");
        Write("movie.nfo");

        Assert.True(FileDeletionService.DeleteWithCompanions(moviePath, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.False(Directory.Exists(_movieDir));
    }

    /// <summary>
    /// Anything that is not plainly title artwork keeps the folder and all its artwork: a stream file,
    /// a media type no list knows, a photo, a user's own text file, a disc structure folder.
    /// True: an unrecognised file is never mistaken for an empty title.
    /// False: a folder still holding a .strm, an .mka, or photos loses its poster and nfo.
    /// </summary>
    [Theory]
    [InlineData("Other.strm")]
    [InlineData("Title (2020).mka")]
    [InlineData("Book.cbz")]
    [InlineData("IMG_0001.jpg")]
    [InlineData("notes.txt")]
    [InlineData("VIDEO_TS/VTS_01_1.VOB")]
    [InlineData("BDMV/index.bdmv")]
    public void DeleteWithCompanions_OtherContentRemains_KeepsEveryTitleFile(string remaining)
    {
        var moviePath = Write("Title (2020).mkv", "movie");
        var poster = Write("poster.jpg");
        var nfo = Write("movie.nfo");
        var other = Write(remaining);

        Assert.True(FileDeletionService.DeleteWithCompanions(moviePath, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.True(File.Exists(poster));
        Assert.True(File.Exists(nfo));
        Assert.True(File.Exists(other));
    }

    /// <summary>
    /// A photo folder: a clip deleted from it leaves every photo, including ones named like artwork.
    /// True: real photos named cover or folder are never taken for title artwork while photos remain.
    /// False: a photo album loses its cover image.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_PhotoFolder_KeepsEveryPhoto()
    {
        var clip = Write("clip.mp4", "video");
        var cover = Write("cover.jpg");
        var folder = Write("folder.jpg");
        var photo = Write("IMG_0001.JPG");

        Assert.True(FileDeletionService.DeleteWithCompanions(clip, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.True(File.Exists(cover));
        Assert.True(File.Exists(folder));
        Assert.True(File.Exists(photo));
    }

    /// <summary>
    /// Title files are recognised whatever the case of their names and extensions.
    /// True: POSTER.JPG and MOVIE.NFO go like poster.jpg and movie.nfo.
    /// False: an upper case name keeps a folder alive forever.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_UppercaseNames_AreRecognised()
    {
        var moviePath = Write("Title (2020).MKV", "movie");
        Write("Title (2020)-THUMB.JPG");
        Write("Title (2020).EN.SRT");
        Write("POSTER.JPG");
        Write("FanArt.PNG");
        Write("MOVIE.NFO");

        Assert.True(FileDeletionService.DeleteWithCompanions(moviePath, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.False(Directory.Exists(_movieDir));
    }

    /// <summary>
    /// The last episode of a season goes with the season's own artwork and folder, while the show and
    /// its other seasons stay as they are.
    /// True: a finished season is cleaned up without touching the rest of the show.
    /// False: the show's artwork or another season goes, or the empty season folder stays.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_LastEpisodeOfASeason_KeepsTheShowAndOtherSeasons()
    {
        var show = Path.Combine(_root, "Show");
        var season1 = Path.Combine(show, "Season 01");
        var season2 = Path.Combine(show, "Season 02");
        Directory.CreateDirectory(season1);
        Directory.CreateDirectory(season2);
        var episode = Path.Combine(season1, "Show - S01E01.mkv");
        File.WriteAllText(episode, "x");
        File.WriteAllText(Path.Combine(season1, "season.nfo"), "x");
        File.WriteAllText(Path.Combine(season1, "folder.jpg"), "x");
        File.WriteAllText(Path.Combine(season2, "Show - S02E01.mkv"), "x");
        File.WriteAllText(Path.Combine(show, "tvshow.nfo"), "x");
        File.WriteAllText(Path.Combine(show, "poster.jpg"), "x");

        Assert.True(FileDeletionService.DeleteWithCompanions(episode, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.False(Directory.Exists(season1));
        Assert.True(File.Exists(Path.Combine(season2, "Show - S02E01.mkv")));
        Assert.True(File.Exists(Path.Combine(show, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(show, "poster.jpg")));
    }

    /// <summary>
    /// A flat show folder (episodes without season folders) whose last top level episode is deleted
    /// keeps its artwork while a subfolder, here specials, still holds media.
    /// True: a subfolder always counts as content.
    /// False: the show loses its poster and nfo while its specials remain.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_FlatShowWithMediaInASubfolder_KeepsTheShowFiles()
    {
        var show = Path.Combine(_root, "Flat Show");
        Directory.CreateDirectory(Path.Combine(show, "Specials"));
        var episode = Path.Combine(show, "Flat Show - E01.mkv");
        File.WriteAllText(episode, "x");
        File.WriteAllText(Path.Combine(show, "tvshow.nfo"), "x");
        File.WriteAllText(Path.Combine(show, "poster.jpg"), "x");
        File.WriteAllText(Path.Combine(show, "Specials", "Flat Show - S00E01.mkv"), "x");

        Assert.True(FileDeletionService.DeleteWithCompanions(episode, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.True(File.Exists(Path.Combine(show, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(show, "poster.jpg")));
    }

    /// <summary>
    /// With the recycling bin on, the item's thumbnail and trickplay move to the bin with it, and the
    /// other version's stay. Expired recycled folders are removed by the retention cleanup, and a
    /// folder the plugin did not put there is left alone.
    /// True: recycling and deleting take the same files, and the bin does not grow forever.
    /// False: thumbnails and trickplay are orphaned in the library, or pile up in the bin.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_RecyclingBin_MovesTheItemsOwnArtifacts()
    {
        var bin = Path.Combine(_tempDir, "bin");
        var moviePath = Write("Title (2020).mkv", "movie");
        Write("Title (2020)-thumb.jpg");
        Write(Path.Combine("Title (2020).trickplay", "320 - 10x10", "0.jpg"));
        var otherThumb = Write("Title (2020) - 4K-thumb.jpg");
        Write("Title (2020) - 4K.mkv");
        var config = Config();
        config.EnableRecyclingBin = true;
        config.RecyclingBinPath = bin;
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(moviePath, new Movie { Name = "Title", Path = moviePath }, libraryManager, config, NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(moviePath));
        Assert.False(File.Exists(Path.Combine(_movieDir, "Title (2020)-thumb.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_movieDir, "Title (2020).trickplay")));
        Assert.True(File.Exists(otherThumb));
        Assert.Equal(2, Directory.GetFiles(bin).Length);
        var recycledFolder = Assert.Single(Directory.GetDirectories(bin));
        Assert.True(File.Exists(Path.Combine(recycledFolder, "320 - 10x10", "0.jpg")));
        Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));

        var expired = Path.Combine(bin, "old.trickplay_2020-01-01_00-00-00.trickplay");
        Directory.CreateDirectory(expired);
        var foreign = Path.Combine(bin, "not-recycled");
        Directory.CreateDirectory(foreign);
        RecyclingBinService.CleanupExpiredFiles(bin, 30, NullLogger.Instance);

        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(recycledFolder));
    }

    /// <summary>
    /// A disc structure item (its path is a folder) is refused with nothing deleted and the item left
    /// in Jellyfin.
    /// True: a DVD or Blu-ray folder is never half deleted and reported gone.
    /// False: the row is dropped while the whole disc folder remains, or Jellyfin deletes it recursively.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_FolderItem_IsRefused()
    {
        var disc = Path.Combine(_movieDir, "VIDEO_TS");
        var vob = Write(Path.Combine("VIDEO_TS", "VTS_01_1.VOB"));
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(null, new Movie { Name = "Title", Path = _movieDir }, libraryManager, Config(removeEmptyFolders: true), NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Contains("folder", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(vob));
        Assert.True(Directory.Exists(disc));
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// A stacked item (part1, part2) is refused with every part left in place.
    /// True: a multi part movie is never reduced to its second half and reported deleted.
    /// False: only the first part goes and the rest is orphaned.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_StackedParts_AreRefused()
    {
        var part1 = Write("Title (2020)-cd1.avi", "1");
        var part2 = Write("Title (2020)-cd2.avi", "2");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();
        var movie = new Movie { Name = "Title", Path = part1, AdditionalParts = new[] { part2 } };

        var result = FileDeletionService.DeleteLocalFile(part1, movie, libraryManager, Config(), NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Contains("2 parts", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(part1));
        Assert.True(File.Exists(part2));
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// A single file item whose file is already gone has its stale Jellyfin entry removed, without
    /// touching the disk, and is reported as gone rather than deleted.
    /// True: a file removed by hand no longer lingers in the library after a delete from the Sync tab.
    /// False: the delete fails, or reports a deletion it did not make.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_FileMissing_RemovesTheStaleEntry()
    {
        var missing = Path.Combine(_movieDir, "Gone (2020).mkv");
        var neighbour = Write("Other (2020).mkv");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(missing, new Movie { Name = "Gone", Path = missing }, libraryManager, Config(removeEmptyFolders: true), NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AlreadyGone);
        var delete = Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
        Assert.False(Assert.IsType<DeleteOptions>(delete.Args.Single(a => a is DeleteOptions)).DeleteFileLocation);
        Assert.True(File.Exists(neighbour));
    }

    /// <summary>
    /// A missing disc structure or stacked item is still refused, entry and all.
    /// True: only a plain single file item is ever treated as already gone.
    /// False: a disc or multi part item whose path moved loses its library entry.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_MissingDiscOrStackedItem_IsStillRefused()
    {
        var missingDisc = Path.Combine(_movieDir, "Disc");
        var missingPart = Path.Combine(_movieDir, "Title (2020)-cd1.avi");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var disc = FileDeletionService.DeleteLocalFile(null, new Movie { Name = "Disc", Path = missingDisc, VideoType = MediaBrowser.Model.Entities.VideoType.Dvd }, libraryManager, Config(), NullLogger.Instance);
        var stacked = FileDeletionService.DeleteLocalFile(missingPart, new Movie { Name = "Title", Path = missingPart, AdditionalParts = new[] { Path.Combine(_movieDir, "Title (2020)-cd2.avi") } }, libraryManager, Config(), NullLogger.Instance);

        Assert.False(disc.Success);
        Assert.Contains("disc structure", disc.ErrorMessage, StringComparison.Ordinal);
        Assert.False(stacked.Success);
        Assert.Contains("2 parts", stacked.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// The item's own path, used when the row tracks none, is checked for a symlinked folder like a
    /// tracked path is.
    /// True: a link inside the library cannot steer a deletion to files outside it.
    /// False: the fallback path deletes through the link.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_ItemPathThroughASymlink_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating a directory link needs extra rights on Windows.
            return;
        }

        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "Linked (2020).mkv");
        File.WriteAllText(target, "not in the library");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Linked"), outside);
        var throughLink = Path.Combine(_root, "Linked", "Linked (2020).mkv");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(null, new Movie { Name = "Linked", Path = throughLink }, libraryManager, Config(), NullLogger.Instance);

        Assert.False(result.Success);
        Assert.Contains("symlinked", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// A file directly in the library root never takes the root's artwork with it, even when it was
    /// the last media there.
    /// True: the cleanup is bounded to a title folder inside the library.
    /// False: the library's own folder.jpg goes with a loose file.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_FileInTheLibraryRoot_KeepsTheRootsArtwork()
    {
        var loose = Path.Combine(_root, "Loose (2001).mkv");
        File.WriteAllText(loose, "movie");
        var rootPoster = Path.Combine(_root, "poster.jpg");
        File.WriteAllText(rootPoster, "library art");

        Assert.True(FileDeletionService.DeleteWithCompanions(loose, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.True(File.Exists(rootPoster));
    }

    /// <summary>
    /// The automatic path deletes an episode with its own thumbnail and leaves the next episode's
    /// thumbnail and the season's artwork while the season still has media.
    /// True: deleting one episode touches only that episode's files.
    /// False: a neighbouring episode loses its images.
    /// </summary>
    [Fact]
    public void DeleteWithCompanions_Episode_KeepsItsNeighbours()
    {
        var season = Path.Combine(_root, "Show", "Season 01");
        Directory.CreateDirectory(season);
        var first = Path.Combine(season, "Show - S01E01.mkv");
        var second = Path.Combine(season, "Show - S01E02.mkv");
        foreach (var file in new[] { first, second, Path.Combine(season, "Show - S01E01-thumb.jpg"), Path.Combine(season, "Show - S01E02-thumb.jpg"), Path.Combine(season, "folder.jpg") })
        {
            File.WriteAllText(file, "x");
        }

        Assert.True(FileDeletionService.DeleteWithCompanions(first, NullLogger.Instance, removeEmptyFolders: true, libraryRootPath: _root));

        Assert.Equal(
            new[] { "Show - S01E02-thumb.jpg", "Show - S01E02.mkv", "folder.jpg" },
            Directory.GetFiles(season).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A row with no tracked path deletes the file the Jellyfin item points at.
    /// True: an item resolved by id is still deleted from disk.
    /// False: the row is reported deleted while the file stays.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_NoTrackedPath_DeletesTheItemsOwnFile()
    {
        var moviePath = Path.Combine(_movieDir, "Title (2020).mkv");
        File.WriteAllText(moviePath, "movie");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();

        var result = FileDeletionService.DeleteLocalFile(null, new Movie { Name = "Title", Path = moviePath }, libraryManager, Config(), NullLogger.Instance);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(moviePath));
        Assert.True(Directory.Exists(_movieDir));
        Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
    }

    /// <summary>
    /// When the file cannot be deleted, Jellyfin is not asked to forget the item.
    /// True: the item and the file stay together and the failure is reported.
    /// False: the library loses an item whose file is still on disk.
    /// </summary>
    [Fact]
    public void DeleteLocalFile_FileCannotBeDeleted_LeavesTheItemInJellyfin()
    {
        if (OperatingSystem.IsWindows())
        {
            // The failure is staged with a read-only folder, which Windows does not enforce on delete.
            return;
        }

        var moviePath = Path.Combine(_movieDir, "Title (2020).mkv");
        File.WriteAllText(moviePath, "movie");
        var (libraryManager, stub) = InterfaceStub.Create<ILibraryManager>();
        File.SetUnixFileMode(_movieDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (Environment.UserName == "root")
            {
                // Root ignores the folder's permissions, so the failure cannot be staged.
                return;
            }

            var result = FileDeletionService.DeleteLocalFile(moviePath, new Movie { Name = "Title", Path = moviePath }, libraryManager, Config(), NullLogger.Instance);

            Assert.False(result.Success);
            Assert.True(File.Exists(moviePath));
            Assert.DoesNotContain(stub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
        }
        finally
        {
            File.SetUnixFileMode(_movieDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
