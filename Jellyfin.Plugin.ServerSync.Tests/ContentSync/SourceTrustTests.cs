using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// What a source server says is never taken on trust where it decides which local file is written,
/// replaced, or later deleted. Each test here is a thing a hostile or broken source could send.
/// </summary>
public sealed class SourceTrustTests : IDisposable
{
    private readonly string _dir;

    public SourceTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

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

    private string Local(string name, int bytes = 10)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static SyncItem? New(string localPath, long sourceSize, ApprovalMode download = ApprovalMode.Enabled, ApprovalMode replace = ApprovalMode.Enabled)
        => SyncStateService.ProcessNewItem(new LibraryMapping(), "id", "/src/film.mkv", sourceSize, DateTime.UtcNow, localPath, download, replace, 0);

    [Fact]
    public void NewItem_FileOfTheSameSize_IsTakenAsSynced()
    {
        var item = New(Local("film.mkv", 10), 10);

        Assert.Equal(SyncStatus.Synced, item!.Status);
    }

    [Fact]
    public void NewItem_SizeNotKnown_NeverAdoptsTheLocalFile()
    {
        // Adopted as Synced, the file would be deleted when the source later dropped the item, though
        // nothing shows it is the source's file.
        var item = New(Local("film.mkv", 10), 0);

        Assert.NotEqual(SyncStatus.Synced, item!.Status);
    }

    [Theory]
    [InlineData(ApprovalMode.Enabled, ApprovalMode.RequireApproval)]
    [InlineData(ApprovalMode.RequireApproval, ApprovalMode.Enabled)]
    public void NewItem_OverADifferentLocalFile_WaitsForApprovalWhenEitherSettingAsks(ApprovalMode download, ApprovalMode replace)
    {
        var item = New(Local("film.mkv", 10), 99, download, replace);

        Assert.Equal(SyncStatus.Pending, item!.Status);
        Assert.Equal(PendingType.Replacement, item.PendingType);
    }

    [Fact]
    public void NewItem_OverADifferentLocalFile_IsLeftAloneWhenChangedFilesIsOff()
    {
        Assert.Null(New(Local("film.mkv", 10), 99, ApprovalMode.Enabled, ApprovalMode.Disabled));
    }

    [Fact]
    public void NewItem_WithNoLocalFile_IsQueued()
    {
        var item = New(Path.Combine(_dir, "absent.mkv"), 99, ApprovalMode.Enabled, ApprovalMode.Disabled);

        Assert.Equal(SyncStatus.Queued, item!.Status);
    }

    [Theory]
    [InlineData("Film.en.srt", "Film.en.srt")]
    [InlineData("/media/Movies/Film/Film.en.forced.ass", "Film.en.forced.ass")]
    [InlineData("D:\\Movies\\Film\\Film.en.srt", "Film.en.srt")]
    [InlineData("film.EN.SRT", "film.EN.SRT")]
    public void CompanionName_KeepsASubtitleOfTheVideo(string remote, string expected)
    {
        Assert.Equal(expected, DownloadService.CompanionNameFor(remote, "Film"));
    }

    [Theory]
    [InlineData("/x/Other Film.mkv")]
    [InlineData("Film.mkv")]
    [InlineData("Film.nfo")]
    [InlineData(".ignore")]
    [InlineData("Other.en.srt")]
    [InlineData("Film2.en.srt")]
    [InlineData("Film.srt/..")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void CompanionName_RefusesAnythingElse(string? remote)
    {
        Assert.Null(DownloadService.CompanionNameFor(remote, "Film"));
    }

    [Fact]
    public void Companions_OfOneTitle_DoNotIncludeAnotherTitleSharingItsPrefix()
    {
        var main = Local("Avatar.mkv");
        var own = Local("Avatar.en.srt");
        Local("Avatar.The.Way.of.Water.mkv");
        var others = Local("Avatar.The.Way.of.Water.en.srt");

        var companions = FileOperationUtilities.GetCompanionFiles(main);

        Assert.Contains(own, companions);
        Assert.DoesNotContain(others, companions);
    }

    [Theory]
    [InlineData("/src/lib/..")]
    [InlineData("/elsewhere/..")]
    [InlineData("/elsewhere/a/")]
    public void TranslatePath_NeverLeavesTheLocalRoot(string sourcePath)
    {
        var translated = Path.GetFullPath(PathUtilities.TranslatePath(sourcePath, "/src/lib", _dir));

        Assert.StartsWith(Path.GetFullPath(_dir), translated, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_IsHeldToAnAllowanceForEachFileAndServer()
    {
        var now = DateTime.UtcNow;
        var sessions = new LinkSessions(() => now);
        var item = Guid.NewGuid();

        Assert.True(sessions.TakeProbe("peer", item, 0));
        Assert.True(sessions.TakeProbe("peer", item, (int)(LinkSessions.ProbeAllowance / 2)));
        Assert.True(sessions.TakeProbe("peer", item, (int)(LinkSessions.ProbeAllowance / 2)));
        Assert.False(sessions.TakeProbe("peer", item, 1));

        // Another file, and another server, each have their own.
        Assert.True(sessions.TakeProbe("peer", Guid.NewGuid(), 1));
        Assert.True(sessions.TakeProbe("other", item, 1));

        // The allowance comes back after an hour.
        now = now.AddHours(1).AddSeconds(1);
        Assert.True(sessions.TakeProbe("peer", item, 1));
    }
}
