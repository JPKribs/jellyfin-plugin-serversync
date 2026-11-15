using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using JPKribs.Jellyfin.Base;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Extensions;

/// <summary>
/// A user row reaches the dashboard with its three values, its image sizes as numbers and as text, and its status.
/// </summary>
public sealed class UserSyncItemExtensionsTests
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static UserSyncItem PolicyRow() => new()
    {
        Id = 3,
        SourceUserId = "src-user",
        LocalUserId = "loc-user",
        SourceUserName = "alice",
        LocalUserName = "alice-here",
        PropertyCategory = UserPropertyCategory.Policy,
        SourceValue = "{\"IsHidden\":true}",
        LocalValue = "{\"IsHidden\":false}",
        MergedValue = "{\"IsHidden\":true}",
        ServerKey = "server-key",
        Status = SyncStatus.Synced,
        StatusDate = At,
        LastSyncTime = At.AddHours(1),
        Reason = "kept"
    };

    /// <summary>
    /// The mapping, the category, the three values, and the status fields copy across.
    /// True: the dashboard shows whose settings the row holds and what each side says.
    /// False: the source, local, and merged columns would be mixed up.
    /// </summary>
    [Fact]
    public void ToDto_PolicyRow_CopiesMappingValuesAndStatus()
    {
        var row = PolicyRow();

        var dto = row.ToDto("https://source.example");

        Assert.Equal((3L, "src-user", "loc-user", "alice", "alice-here"), (dto.Id, dto.SourceUserId, dto.LocalUserId, dto.SourceUserName, dto.LocalUserName));
        Assert.Equal(UserPropertyCategory.Policy, dto.PropertyCategory);
        Assert.Equal(("{\"IsHidden\":true}", "{\"IsHidden\":false}", "{\"IsHidden\":true}"), (dto.SourceValue, dto.LocalValue, dto.MergedValue));
        Assert.Equal(("Synced", "kept", "https://source.example", "server-key"), (dto.Status, dto.ErrorMessage, dto.SourceServerUrl, dto.ServerKey));
        Assert.Equal((At, At.AddHours(1)), (dto.StatusDate, dto.LastSyncTime));
    }

    /// <summary>
    /// The change flag and its summary are the row's own.
    /// True: a row whose merged and local values differ in one field says so.
    /// False: the badge would disagree with what the next run applies.
    /// </summary>
    [Fact]
    public void ToDto_OneFieldDiffers_ReportsOneDifference()
    {
        var row = PolicyRow();

        var dto = row.ToDto();

        Assert.True(dto.HasChanges);
        Assert.Equal("1 difference", dto.ChangesSummary);
        Assert.Null(dto.SourceServerUrl);
    }

    /// <summary>
    /// An image size is carried as a number and as readable text, the largest size included.
    /// True: the profile image row shows a size a person can read on each side.
    /// False: the dashboard would show raw byte counts or nothing.
    /// </summary>
    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(2048L, 5242880L)]
    [InlineData(long.MaxValue, 1L)]
    public void ToDto_ImageSizesSet_FormatsEachSide(long sourceSize, long localSize)
    {
        var row = new UserSyncItem { PropertyCategory = UserPropertyCategory.ProfileImage, SourceImageSize = sourceSize, LocalImageSize = localSize };

        var dto = row.ToDto();

        Assert.Equal((sourceSize, localSize), (dto.SourceImageSize, dto.LocalImageSize));
        Assert.Equal(FormatUtilities.FormatBytes(sourceSize), dto.SourceImageSizeFormatted);
        Assert.Equal(FormatUtilities.FormatBytes(localSize), dto.LocalImageSizeFormatted);
    }

    /// <summary>
    /// A side with no image has no size and no size text.
    /// True: a user with no profile image shows an empty cell.
    /// False: a missing image would show as "0 B", as if an empty file existed.
    /// </summary>
    [Fact]
    public void ToDto_NoImageSizes_LeavesSizeTextEmpty()
    {
        var row = new UserSyncItem { PropertyCategory = UserPropertyCategory.ProfileImage };

        var dto = row.ToDto();

        Assert.Null(dto.SourceImageSize);
        Assert.Null(dto.LocalImageSize);
        Assert.Null(dto.SourceImageSizeFormatted);
        Assert.Null(dto.LocalImageSizeFormatted);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the page would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void ToDto_NullRow_Throws()
    {
        UserSyncItem? row = null;

        Assert.Throws<ArgumentNullException>(() => row!.ToDto());
    }
}
