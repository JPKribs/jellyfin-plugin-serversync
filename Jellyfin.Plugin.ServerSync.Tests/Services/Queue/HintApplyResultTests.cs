using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Each way an apply can end carries the outcome the worker acts on and the code the origin is told.
/// </summary>
public sealed class HintApplyResultTests
{
    /// <summary>
    /// A plain apply is an applied outcome with the applied code and nothing else.
    /// True: the origin hears its change was written.
    /// False: a written change is reported as kept or dropped.
    /// </summary>
    [Fact]
    public void Applied_PlainApply_CarriesAppliedCodeAndNoReason()
    {
        var result = HintApplyResult.Applied;

        Assert.Equal(HintApplyOutcome.Applied, result.Outcome);
        Assert.Equal(HintCode.Applied, result.Code);
        Assert.Null(result.Reason);
        Assert.Null(result.ItemTitle);
        Assert.Null(result.ParkKey);
    }

    /// <summary>
    /// An apply to an item that is not known is the plain apply.
    /// True: a missing title never fails a finished apply.
    /// False: the worker would throw after the value was already written.
    /// </summary>
    [Fact]
    public void AppliedTo_NullItem_IsThePlainApply()
    {
        var result = HintApplyResult.AppliedTo(null);

        Assert.Equal(HintApplyResult.Applied, result);
    }

    /// <summary>
    /// An apply to a known item carries the item's Jellyfin title and line.
    /// True: the Activity page names the item the way the library does.
    /// False: the entry names nothing, or names the item by its path.
    /// </summary>
    [Fact]
    public void AppliedTo_KnownItem_CarriesItsTitleAndLine()
    {
        var movie = new Movie { Name = "Only A (2021)" };

        var result = HintApplyResult.AppliedTo(movie);

        Assert.Equal(HintApplyOutcome.Applied, result.Outcome);
        Assert.Equal(HintCode.Applied, result.Code);
        Assert.Equal(ItemTitles.TitleOf(movie), result.ItemTitle);
        Assert.Equal(ItemTitles.LineOf(movie), result.ItemLine);
    }

    /// <summary>
    /// An apply with nothing to write is unchanged, with the unchanged code.
    /// True: the row is finished and the origin hears nothing was needed.
    /// False: a matching value is retried or counted as written.
    /// </summary>
    [Fact]
    public void Unchanged_NothingToWrite_CarriesUnchangedCode()
    {
        var result = HintApplyResult.Unchanged;

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.Unchanged, result.Code);
        Assert.Null(result.Reason);
    }

    /// <summary>
    /// A kept value is an unchanged outcome that carries the code and reason given.
    /// True: the origin learns why its value was not taken, and the row is finished.
    /// False: a kept value is retried forever or dropped without a reason.
    /// </summary>
    [Fact]
    public void Kept_LocalValueStays_IsUnchangedWithTheGivenCodeAndReason()
    {
        var result = HintApplyResult.Kept(HintCode.Superseded, "this server holds a newer edit");

        Assert.Equal(HintApplyOutcome.Unchanged, result.Outcome);
        Assert.Equal(HintCode.Superseded, result.Code);
        Assert.Equal("this server holds a newer edit", result.Reason);
    }

    /// <summary>
    /// A dropped hint carries the code and reason given.
    /// True: the origin is told exactly why the hint cannot apply here.
    /// False: every drop looks the same and the sender cannot hold the object back.
    /// </summary>
    [Fact]
    public void Dropped_CannotApply_CarriesTheGivenCodeAndReason()
    {
        var result = HintApplyResult.Dropped(HintCode.NotMapped, "the path is not mapped");

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Equal(HintCode.NotMapped, result.Code);
        Assert.Equal("the path is not mapped", result.Reason);
    }

    /// <summary>
    /// A retry keeps the queued code, since the hint is still held here.
    /// True: the origin sees the hint as still waiting and does not send it again.
    /// False: a failing hint is reported finished and its change is lost.
    /// </summary>
    [Fact]
    public void RetryLater_FailedForNow_IsARetryThatStaysQueued()
    {
        var result = HintApplyResult.RetryLater("the origin did not answer");

        Assert.Equal(HintApplyOutcome.Retry, result.Outcome);
        Assert.Equal(HintCode.Queued, result.Code);
        Assert.Equal("the origin did not answer", result.Reason);
    }

    /// <summary>
    /// A parked hint carries its park key and the no local item code.
    /// True: the hint wakes when the item it waits for appears.
    /// False: the hint has no key to wake under and waits out its whole window.
    /// </summary>
    [Fact]
    public void Park_NoLocalItemYet_CarriesTheParkKey()
    {
        var result = HintApplyResult.Park("path:/local/movies/a.mkv", "no local item yet");

        Assert.Equal(HintApplyOutcome.Parked, result.Outcome);
        Assert.Equal(HintCode.NoLocalItem, result.Code);
        Assert.Equal("path:/local/movies/a.mkv", result.ParkKey);
        Assert.Equal("no local item yet", result.Reason);
    }
}
