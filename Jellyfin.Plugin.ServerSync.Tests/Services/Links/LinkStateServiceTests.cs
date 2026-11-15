using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link state rules must give each link row the status its file, its settings, and the user's choices call for on a refresh.
/// </summary>
public sealed class LinkStateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linkstate-" + Guid.NewGuid().ToString("N"));
    private readonly LinkFiles _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkStateServiceTests"/> class.
    /// </summary>
    public LinkStateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _files = LinkTestFiles.Create(_dir);
    }

    /// <inheritdoc/>
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

    /// <summary>
    /// With downloads switched off no row is made for a new link.
    /// True: a source file is not tracked while new items are disabled.
    /// False: links would appear with the setting off.
    /// </summary>
    [Fact]
    public void ProcessNewLink_DownloadsDisabled_MakesNoRow()
    {
        var mapping = new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l" };

        var row = LinkStateService.ProcessNewLink(mapping, "id", "/src/Film.mkv", 10, DateTime.UtcNow, Path.Combine(_dir, "Film.strm"), ApprovalMode.Disabled, _files);

        Assert.Null(row);
    }

    /// <summary>
    /// A new link's row carries the mapping's libraries, the source's details, and Link storage.
    /// True: the row says where the link came from and that it is a link.
    /// False: a new link would be tracked as a copy and downloaded in full.
    /// </summary>
    /// <param name="mode">The approval setting for new items.</param>
    /// <param name="status">The status the row starts in.</param>
    /// <param name="pending">The pending type the row starts with.</param>
    [Theory]
    [InlineData(ApprovalMode.Enabled, SyncStatus.Queued, null)]
    [InlineData(ApprovalMode.RequireApproval, SyncStatus.Pending, PendingType.Download)]
    public void ProcessNewLink_NewFile_FillsTheRow(ApprovalMode mode, SyncStatus status, PendingType? pending)
    {
        var mapping = new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l" };
        var created = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(_dir, "Film.strm");

        var row = LinkStateService.ProcessNewLink(mapping, "id", "/src/Film.mkv", 10, created, path, mode, _files);

        Assert.NotNull(row);
        Assert.Equal(("s", "l", "id", "/src/Film.mkv", 10L, created, path), (row.SourceLibraryId, row.LocalLibraryId, row.SourceItemId, row.SourcePath, row.SourceSize, row.SourceCreateDate, row.LocalPath));
        Assert.Equal(StorageMode.Link, row.StorageMode);
        Assert.Equal(status, row.Status);
        Assert.Equal(pending, row.PendingType);
    }

    /// <summary>
    /// The new link and existing link rules refuse a missing mapping, row, link files, or logger.
    /// True: the mistake is named where it was made.
    /// False: a null would surface as a failure halfway through a refresh.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var mapping = new LibraryMapping();
        var row = new SyncItem();

        Assert.Throws<ArgumentNullException>(() => LinkStateService.ProcessNewLink(null!, "id", "/s", 1, DateTime.UtcNow, "/l.strm", ApprovalMode.Enabled, _files));
        Assert.Throws<ArgumentNullException>(() => LinkStateService.ProcessNewLink(mapping, "id", "/s", 1, DateTime.UtcNow, "/l.strm", ApprovalMode.Enabled, null!));
        Assert.Throws<ArgumentNullException>(() => LinkStateService.ProcessExistingLink(null!, "/s", 1, DateTime.UtcNow, "/l.strm", _files, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => LinkStateService.ProcessExistingLink(row, "/s", 1, DateTime.UtcNow, "/l.strm", null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => LinkStateService.ProcessExistingLink(row, "/s", 1, DateTime.UtcNow, "/l.strm", _files, null!));
        Assert.Throws<ArgumentNullException>(() => LinkStateService.MarkPromoting(null!, false));
    }

    /// <summary>
    /// A link waiting to be deleted whose file is back on the source is queued to be written again.
    /// True: a film that returns before its removal ran keeps its link.
    /// False: the link would be deleted although the source has the file.
    /// </summary>
    /// <param name="status">The row's status.</param>
    /// <param name="pending">The row's pending type.</param>
    [Theory]
    [InlineData(SyncStatus.Deleting, null)]
    [InlineData(SyncStatus.Pending, PendingType.Deletion)]
    public void ProcessExistingLink_BackOnTheSource_IsQueuedAgain(SyncStatus status, PendingType? pending)
    {
        var row = Row(status);
        row.PendingType = pending;

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance);

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Null(row.PendingType);
    }

    /// <summary>
    /// A link waiting for approval stays waiting, even with its .strm in place.
    /// True: only the user's approval moves a pending link on.
    /// False: a refresh would approve links by itself.
    /// </summary>
    [Fact]
    public void ProcessExistingLink_PendingApproval_StaysPending()
    {
        var row = Row(SyncStatus.Pending);
        row.PendingType = PendingType.Download;
        _files.Write(row.LocalPath!, row.SourceItemId, replace: false);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance);

        Assert.Equal(SyncStatus.Pending, row.Status);
        Assert.Equal(PendingType.Download, row.PendingType);
    }

    /// <summary>
    /// A link whose promotion was waiting for approval goes back to a queued link when its mapping links again.
    /// True: switching a mapping back to Link cancels the copies nobody approved.
    /// False: the rows would wait for an approval that no longer means anything.
    /// </summary>
    [Fact]
    public void ProcessExistingLink_PromotionNoLongerWanted_BecomesALinkAgain()
    {
        var row = Row(SyncStatus.Pending, StorageMode.Promoting);
        row.PendingType = PendingType.Replacement;

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance, promoteMode: null);

        Assert.Equal(StorageMode.Link, row.StorageMode);
        Assert.Null(row.PendingType);
        Assert.NotEqual(SyncStatus.Pending, row.Status);
    }

    /// <summary>
    /// A promoting row that is queued, or still wanted and pending, is left as it is.
    /// True: a copy under way is not restarted by a refresh.
    /// False: every refresh would reset the promotion and it would never finish.
    /// </summary>
    /// <param name="status">The row's status.</param>
    /// <param name="wanted">Whether the mapping still copies.</param>
    [Theory]
    [InlineData(SyncStatus.Queued, true)]
    [InlineData(SyncStatus.Queued, false)]
    [InlineData(SyncStatus.Pending, true)]
    public void ProcessExistingLink_PromotionUnderWay_IsLeftAlone(SyncStatus status, bool wanted)
    {
        var row = Row(status, StorageMode.Promoting);
        row.PendingType = status == SyncStatus.Pending ? PendingType.Replacement : null;

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance, wanted ? ApprovalMode.RequireApproval : null);

        Assert.Equal(StorageMode.Promoting, row.StorageMode);
        Assert.Equal(status, row.Status);
    }

    /// <summary>
    /// A promoting row marked synced by hand goes back to being a link.
    /// True: the next refresh asks about the promotion again from a clean state.
    /// False: a row would claim to be a finished copy that was never downloaded.
    /// </summary>
    [Fact]
    public void ProcessExistingLink_PromotingMarkedSynced_BecomesALinkAgain()
    {
        var row = Row(SyncStatus.Synced, StorageMode.Promoting);
        row.PendingType = PendingType.Replacement;

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance, ApprovalMode.Enabled);

        Assert.Equal(StorageMode.Link, row.StorageMode);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Null(row.PendingType);
    }

    /// <summary>
    /// Marking a link to become a copy sets it promoting, waiting for approval when that is required, with its retries cleared.
    /// True: a link under a Copy mapping is downloaded, after approval when asked for.
    /// False: the link would stay a link, or download without the approval the user set.
    /// </summary>
    /// <param name="requireApproval">Whether the copy needs approval.</param>
    /// <param name="status">The status the row takes.</param>
    /// <param name="pending">The pending type the row takes.</param>
    [Theory]
    [InlineData(false, SyncStatus.Queued, null)]
    [InlineData(true, SyncStatus.Pending, PendingType.Replacement)]
    public void MarkPromoting_Link_BecomesPromoting(bool requireApproval, SyncStatus status, PendingType? pending)
    {
        var row = Row(SyncStatus.Errored);
        row.RetryCount = 5;

        var marked = LinkStateService.MarkPromoting(row, requireApproval);

        Assert.True(marked);
        Assert.Equal(StorageMode.Promoting, row.StorageMode);
        Assert.Equal(status, row.Status);
        Assert.Equal(pending, row.PendingType);
        Assert.Equal(0, row.RetryCount);
    }

    /// <summary>
    /// A row that is already a copy is not marked and does not change.
    /// True: only links are ever promoted.
    /// False: a synced copy would be queued to download over itself.
    /// </summary>
    [Fact]
    public void MarkPromoting_Copy_ChangesNothing()
    {
        var row = Row(SyncStatus.Synced, StorageMode.Copy);

        var marked = LinkStateService.MarkPromoting(row, requireApproval: true);

        Assert.False(marked);
        Assert.Equal(StorageMode.Copy, row.StorageMode);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Null(row.PendingType);
    }

    private SyncItem Row(SyncStatus status, StorageMode mode = StorageMode.Link)
        => LinkTestTable.Row("aaaa", Path.Combine(_dir, "Film.strm"), mode, status, "/src/Film.mkv");
}
