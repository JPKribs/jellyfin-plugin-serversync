using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Extensions;

/// <summary>
/// A history row reaches the dashboard with every side of its state, its status as text, and its reason as the error.
/// </summary>
public sealed class HistorySyncItemExtensionsTests
{
    private static readonly DateTime Played = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static HistorySyncItem Row() => new()
    {
        Id = 7,
        SourceUserId = "src-user",
        LocalUserId = "loc-user",
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "src-item",
        LocalItemId = "loc-item",
        ItemName = "Film",
        SourcePath = "/media/movies/film.mkv",
        LocalPath = "/local/movies/film.mkv",
        SourceIsPlayed = true,
        SourcePlayCount = 3,
        SourcePlaybackPositionTicks = 100,
        SourceLastPlayedDate = Played,
        SourceIsFavorite = true,
        LocalIsPlayed = false,
        LocalPlayCount = 1,
        LocalPlaybackPositionTicks = 50,
        LocalLastPlayedDate = Played.AddDays(-1),
        LocalIsFavorite = false,
        MergedIsPlayed = true,
        MergedPlayCount = 3,
        MergedPlaybackPositionTicks = 100,
        MergedLastPlayedDate = Played,
        MergedIsFavorite = true,
        ServerKey = "server-key",
        Status = SyncStatus.Errored,
        StatusDate = Played.AddHours(1),
        LastSyncTime = Played.AddHours(2),
        Reason = "user data write failed"
    };

    /// <summary>
    /// The identity, paths, and all three sides of the state copy across unchanged.
    /// True: the dashboard shows the source, local, and merged values the row holds.
    /// False: a column would show another side's value and mislead whoever reads the conflict.
    /// </summary>
    [Fact]
    public void ToDto_FullRow_CopiesIdentityAndEverySide()
    {
        var row = Row();

        var dto = row.ToDto("https://source.example");

        Assert.Equal(7, dto.Id);
        Assert.Equal(("src-user", "loc-user", "src-lib", "loc-lib"), (dto.SourceUserId, dto.LocalUserId, dto.SourceLibraryId, dto.LocalLibraryId));
        Assert.Equal(("src-item", "loc-item", "Film"), (dto.SourceItemId, dto.LocalItemId, dto.ItemName));
        Assert.Equal(("/media/movies/film.mkv", "/local/movies/film.mkv"), (dto.SourcePath, dto.LocalPath));
        Assert.Equal((true, 3, 100L, Played, true), (dto.SourceIsPlayed, dto.SourcePlayCount, dto.SourcePlaybackPositionTicks, dto.SourceLastPlayedDate, dto.SourceIsFavorite));
        Assert.Equal((false, 1, 50L, Played.AddDays(-1), false), (dto.LocalIsPlayed, dto.LocalPlayCount, dto.LocalPlaybackPositionTicks, dto.LocalLastPlayedDate, dto.LocalIsFavorite));
        Assert.Equal((true, 3, 100L, Played, true), (dto.MergedIsPlayed, dto.MergedPlayCount, dto.MergedPlaybackPositionTicks, dto.MergedLastPlayedDate, dto.MergedIsFavorite));
    }

    /// <summary>
    /// The status goes out as its name, the reason as the error, and the server as given.
    /// True: the dashboard can label the row, show why it failed, and link to its source.
    /// False: the status would be a bare number and a failed row would show no cause.
    /// </summary>
    [Fact]
    public void ToDto_ErroredRow_CarriesStatusTextReasonAndServer()
    {
        var row = Row();

        var dto = row.ToDto("https://source.example");

        Assert.Equal("Errored", dto.Status);
        Assert.Equal("user data write failed", dto.ErrorMessage);
        Assert.Equal("https://source.example", dto.SourceServerUrl);
        Assert.Equal("server-key", dto.ServerKey);
        Assert.Equal(Played.AddHours(1), dto.StatusDate);
        Assert.Equal(Played.AddHours(2), dto.LastSyncTime);
    }

    /// <summary>
    /// The change flag is the row's own, true when merged and local differ and false when they match.
    /// True: the badge on the dashboard agrees with what the next run will apply.
    /// False: a settled row would show as changed, or a waiting one as done.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ToDto_MergedAgainstLocal_ReportsHasChanges(bool localIsPlayed, bool expected)
    {
        var row = new HistorySyncItem { MergedIsPlayed = true, LocalIsPlayed = localIsPlayed };

        var dto = row.ToDto(null);

        Assert.Equal(expected, dto.HasChanges);
    }

    /// <summary>
    /// A row with nothing set and no source address maps without failing and leaves the empty fields empty.
    /// True: a row built before its first scan still lists.
    /// False: one sparse row would break the whole page.
    /// </summary>
    [Fact]
    public void ToDto_EmptyRowAndNullUrl_LeavesFieldsEmpty()
    {
        var row = new HistorySyncItem();

        var dto = row.ToDto(null);

        Assert.Null(dto.SourceServerUrl);
        Assert.Null(dto.ItemName);
        Assert.Null(dto.SourceIsPlayed);
        Assert.Null(dto.ErrorMessage);
        Assert.Equal("Pending", dto.Status);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the page would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void ToDto_NullRow_Throws()
    {
        HistorySyncItem? row = null;

        Assert.Throws<ArgumentNullException>(() => row!.ToDto("https://source.example"));
    }
}
