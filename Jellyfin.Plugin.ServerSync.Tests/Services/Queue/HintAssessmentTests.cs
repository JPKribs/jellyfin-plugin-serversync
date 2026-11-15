using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// An assessment says to store a hint only when it can apply now or once its local object appears.
/// </summary>
public sealed class HintAssessmentTests
{
    /// <summary>
    /// Only a queued hint and one waiting for its local item are kept.
    /// True: a hint that can never apply here is answered at once and takes no room in the queue.
    /// False: declined hints are stored and retried, or a hint that could apply is thrown away.
    /// </summary>
    [Theory]
    [InlineData(HintCode.Queued, true)]
    [InlineData(HintCode.NoLocalItem, true)]
    [InlineData(HintCode.AlreadyCurrent, false)]
    [InlineData(HintCode.ModuleOff, false)]
    [InlineData(HintCode.NotMapped, false)]
    [InlineData(HintCode.Filtered, false)]
    [InlineData(HintCode.Invalid, false)]
    [InlineData(HintCode.FutureVersion, false)]
    [InlineData(HintCode.Unsupported, false)]
    [InlineData(HintCode.Applied, false)]
    [InlineData(HintCode.Unchanged, false)]
    [InlineData(HintCode.Superseded, false)]
    [InlineData(HintCode.Locked, false)]
    [InlineData(HintCode.AwaitingApproval, false)]
    [InlineData(HintCode.Ignored, false)]
    [InlineData(HintCode.SourceGone, false)]
    [InlineData(HintCode.Unreadable, false)]
    [InlineData(HintCode.Priority, false)]
    [InlineData(HintCode.GaveUp, false)]
    public void Keep_EachCode_KeepsOnlyWhatCanStillApply(HintCode code, bool kept)
    {
        var assessment = new HintAssessment(code, null);

        var keep = assessment.Keep;

        Assert.Equal(kept, keep);
    }

    /// <summary>
    /// An assessment built without a park key has none.
    /// True: a hint that is simply queued is due at once.
    /// False: a queued hint is parked under an empty key and never wakes.
    /// </summary>
    [Fact]
    public void ParkKey_NotGiven_IsNull()
    {
        var assessment = new HintAssessment(HintCode.Queued, null);

        Assert.Null(assessment.ParkKey);
        Assert.Null(assessment.Reason);
    }

    /// <summary>
    /// An assessment carries the reason and park key it was built with.
    /// True: the receiver parks the hint under the key triage chose.
    /// False: the park key is lost between triage and the store.
    /// </summary>
    [Fact]
    public void Constructor_WithReasonAndParkKey_CarriesBoth()
    {
        var assessment = new HintAssessment(HintCode.NoLocalItem, "no local item yet", "path:/local/movies/a.mkv");

        Assert.Equal(HintCode.NoLocalItem, assessment.Code);
        Assert.Equal("no local item yet", assessment.Reason);
        Assert.Equal("path:/local/movies/a.mkv", assessment.ParkKey);
    }
}
