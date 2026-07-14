using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Boundary checks that stand between the deletion pipeline and files outside
/// the user's libraries. Uses real temp directories (and real symlinks where
/// the platform allows) because these guards exist precisely for filesystem
/// edge cases a mock can't reproduce.
/// </summary>
public sealed class FileValidationServiceTests : IDisposable
{
    private readonly string _root;

    public FileValidationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "serversync-fv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PluginConfiguration ConfigWithRoots(params string[] roots)
    {
        var config = new PluginConfiguration();
        foreach (var root in roots)
        {
            config.LibraryMappings.Add(new LibraryMapping
            {
                IsEnabled = true,
                SourceLibraryId = Guid.NewGuid().ToString(),
                LocalLibraryId = Guid.NewGuid().ToString(),
                LocalRootPath = root
            });
        }

        return config;
    }

    /// <summary>
    /// The classic prefix trap: "/media/videos_evil" must not pass as inside
    /// "/media/videos".
    /// </summary>
    [Fact]
    public void IsPathWithinLibrary_SiblingWithSharedPrefix_Rejected()
    {
        var lib = Path.Combine(_root, "videos");
        Directory.CreateDirectory(lib);
        var config = ConfigWithRoots(lib);

        Assert.False(FileValidationService.IsPathWithinLibrary(lib + "_evil/movie.mkv", config));
        Assert.True(FileValidationService.IsPathWithinLibrary(Path.Combine(lib, "movie.mkv"), config));
    }

    /// <summary>
    /// Disabled mappings must not vouch for a path — a file under a disabled
    /// mapping's root is out of bounds for deletion.
    /// </summary>
    [Fact]
    public void IsPathWithinLibrary_DisabledMapping_DoesNotMatch()
    {
        var lib = Path.Combine(_root, "videos");
        var config = ConfigWithRoots(lib);
        config.LibraryMappings[0].IsEnabled = false;

        Assert.False(FileValidationService.IsPathWithinLibrary(Path.Combine(lib, "movie.mkv"), config));
    }

    /// <summary>
    /// Traversal sequences resolve before comparison; escaping via ".." from
    /// inside a library must be caught.
    /// </summary>
    [Fact]
    public void IsPathWithinLibrary_DotDotEscape_Rejected()
    {
        var lib = Path.Combine(_root, "videos");
        var config = ConfigWithRoots(lib);

        Assert.False(FileValidationService.IsPathWithinLibrary(Path.Combine(lib, "..", "other", "movie.mkv"), config));
    }

    /// <summary>
    /// GetContainingLibraryRoot returns the actual matching root (the symlink
    /// walk needs the right boundary) and null for outsiders.
    /// </summary>
    [Fact]
    public void GetContainingLibraryRoot_ReturnsMatchingRootOrNull()
    {
        var libA = Path.Combine(_root, "a");
        var libB = Path.Combine(_root, "b");
        var config = ConfigWithRoots(libA, libB);

        Assert.Equal(libB, FileValidationService.GetContainingLibraryRoot(Path.Combine(libB, "x.mkv"), config));
        Assert.Null(FileValidationService.GetContainingLibraryRoot(Path.Combine(_root, "c", "x.mkv"), config));
        Assert.Null(FileValidationService.GetContainingLibraryRoot(null, config));
    }

    /// <summary>
    /// A plain nested directory is not flagged — the guard must not block
    /// ordinary deletions.
    /// </summary>
    [Fact]
    public void HasSymlinkedDirectoryComponent_PlainDirectories_False()
    {
        var lib = Path.Combine(_root, "videos");
        var nested = Path.Combine(lib, "show", "season1");
        Directory.CreateDirectory(nested);

        Assert.False(FileValidationService.HasSymlinkedDirectoryComponent(Path.Combine(nested, "e1.mkv"), lib));
    }

    /// <summary>
    /// A symlinked directory between the file and the root IS flagged: the
    /// physical file may live outside the library even though the lexical
    /// path is inside it. Skipped on platforms where the test can't create
    /// symlinks (e.g. Windows without developer mode).
    /// </summary>
    [Fact]
    public void HasSymlinkedDirectoryComponent_LinkedDirInsideRoot_True()
    {
        var lib = Path.Combine(_root, "videos");
        var outside = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(outside);

        var link = Path.Combine(lib, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // platform can't create symlinks; nothing to verify here
        }

        Assert.True(FileValidationService.HasSymlinkedDirectoryComponent(Path.Combine(link, "movie.mkv"), lib));
    }

    /// <summary>
    /// The file itself being a symlink is fine — deleting a link leaves its
    /// target intact — only directory components are dangerous.
    /// </summary>
    [Fact]
    public void HasSymlinkedDirectoryComponent_FileIsLink_False()
    {
        var lib = Path.Combine(_root, "videos");
        Directory.CreateDirectory(lib);
        var target = Path.Combine(_root, "target.mkv");
        File.WriteAllText(target, "x");

        var link = Path.Combine(lib, "movie.mkv");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(FileValidationService.HasSymlinkedDirectoryComponent(link, lib));
    }
}
