using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>What an interrupted replacement set aside is put back, and nothing else is touched.</summary>
public sealed class ReplacementRecoveryTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-recovery-" + Guid.NewGuid().ToString("N"));

    public ReplacementRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// A server stopped after the old file was set aside and before the new one was moved in.
    /// True: the film and its subtitles and artwork are back under their own names.
    /// False: the film is missing from the library for good, with its file sitting beside it.
    /// </summary>
    [Fact]
    public void Restore_PutsBackTheFileAndWhatGoesWithIt()
    {
        var film = Path.Combine(_dir, "Film.mkv");
        Write("Film.mkv.replacing." + Id, "film");
        Write("Film.en.srt.replacing." + Id, "subs");
        Write("Film-poster.jpg.replacing." + Id, "art");

        Assert.Equal(3, ReplacementRecovery.Restore(film, NullLogger.Instance));

        Assert.Equal("film", File.ReadAllText(film));
        Assert.Equal("subs", File.ReadAllText(Path.Combine(_dir, "Film.en.srt")));
        Assert.Equal("art", File.ReadAllText(Path.Combine(_dir, "Film-poster.jpg")));
    }

    /// <summary>
    /// The new file made it in and only the files that go with it were left set aside, and a file that
    /// is already in place is never written over.
    /// True: local subtitles come back, and the file in place stays the file in place.
    /// False: a restore would overwrite the new film with the old, or lose the local subtitles.
    /// </summary>
    [Fact]
    public void Restore_NeverWritesOverAFileInPlace()
    {
        var film = Write("Film.mkv", "new");
        var aside = Write("Film.mkv.replacing." + Id, "old");
        Write("Film.en.srt", "new subs");
        var subsAside = Write("Film.en.srt.replacing." + Id, "old subs");
        Write("Film.nfo.replacing." + Id, "nfo");

        Assert.Equal(1, ReplacementRecovery.Restore(film, NullLogger.Instance));

        Assert.Equal("new", File.ReadAllText(film));
        Assert.Equal("new subs", File.ReadAllText(Path.Combine(_dir, "Film.en.srt")));
        Assert.True(File.Exists(aside));
        Assert.True(File.Exists(subsAside));
        Assert.Equal("nfo", File.ReadAllText(Path.Combine(_dir, "Film.nfo")));
    }

    /// <summary>
    /// Another film's files in the same folder, and names that only look like set-aside files.
    /// True: only this film's files are moved.
    /// False: a restore for one film would rename files that belong to another.
    /// </summary>
    [Fact]
    public void Restore_LeavesOtherFilmsAndOtherNamesAlone()
    {
        var film = Write("Film.mkv", "x");
        var other = Write("Filmography.mkv.replacing." + Id);
        var unrelated = Write("Other.srt.replacing." + Id);
        var notOurs = Write("Film.srt.replacing.notanid");

        Assert.Equal(0, ReplacementRecovery.Restore(film, NullLogger.Instance));

        Assert.True(File.Exists(other));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(notOurs));
    }
}
