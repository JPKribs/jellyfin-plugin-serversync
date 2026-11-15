using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// A refusing verdict must carry its reason and never an item or a user to stream as.
/// </summary>
public sealed class LinkVerdictTests
{
    /// <summary>
    /// A refusal is not allowed, keeps the reason, and names nothing to stream.
    /// True: a refused stream cannot be served by reading the verdict's item.
    /// False: a refusal could still hand the caller a file and a user.
    /// </summary>
    [Fact]
    public void Refuse_WithReason_NamesNoItemOrUser()
    {
        const string Reason = "the file is no longer on the source";

        var verdict = LinkVerdict.Refuse(Reason);

        Assert.False(verdict.Allowed);
        Assert.Equal(Reason, verdict.Reason);
        Assert.Null(verdict.Item);
        Assert.Null(verdict.UserId);
    }
}
