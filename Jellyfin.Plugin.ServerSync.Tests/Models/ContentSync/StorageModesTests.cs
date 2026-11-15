using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.ContentSync;

/// <summary>
/// A storage mode is a link exactly when the item is streamed from its source or is becoming one.
/// </summary>
public class StorageModesTests
{
    /// <summary>
    /// Only Link and Promoting count as a link.
    /// True: an item being promoted to a link is still treated as a link until it finishes.
    /// False: a copy would be handled as a link, or a promoting link would be handled as a copy.
    /// </summary>
    [Theory]
    [InlineData(StorageMode.Copy, false)]
    [InlineData(StorageMode.Link, true)]
    [InlineData(StorageMode.Promoting, true)]
    [InlineData(StorageMode.Unkeeping, false)]
    public void IsLink_ByMode_MatchesLinkAndPromoting(StorageMode mode, bool expected)
    {
        var result = mode.IsLink();

        Assert.Equal(expected, result);
    }
}
