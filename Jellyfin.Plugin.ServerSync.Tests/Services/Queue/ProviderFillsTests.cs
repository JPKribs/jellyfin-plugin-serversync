using System;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// A value the metadata provider filled from a source is remembered by path or by person for a few minutes, and nothing else is.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class ProviderFillsTests
{
    /// <summary>
    /// A fill with no path or no name remembers nothing.
    /// True: an item without a path never matches other pathless items.
    /// False: one pathless fill silences the announcement of every pathless item.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MarkPath_NoPath_RemembersNothing(string? path)
    {
        ProviderFills.MarkPath(path);

        Assert.False(ProviderFills.WasFilled(path, null, DateTime.UtcNow));
    }

    /// <summary>
    /// A fill with a blank person name remembers nothing.
    /// True: a nameless person never matches.
    /// False: every blank name counts as filled and real edits are not announced.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkPerson_BlankName_RemembersNothing(string? name)
    {
        ProviderFills.MarkPerson(name);

        Assert.False(ProviderFills.WasFilled(null, name, DateTime.UtcNow));
    }

    /// <summary>
    /// A path filled with one separator is found with the other, in any case.
    /// True: the save event for a filled item is recognised however Jellyfin spells its path.
    /// False: a value copied from a source is announced back to the pool.
    /// </summary>
    [Fact]
    public void WasFilled_OtherSeparatorAndCase_FindsThePath()
    {
        var name = Guid.NewGuid().ToString("N");
        ProviderFills.MarkPath("D:\\Media\\Movies\\" + name + ".mkv");

        var found = ProviderFills.WasFilled("d:/media/movies/" + name + ".MKV", null, DateTime.UtcNow);

        Assert.True(found);
    }

    /// <summary>
    /// A filled person does not make a path of the same text count as filled.
    /// True: paths and people are remembered apart.
    /// False: a person named like a path silences that item.
    /// </summary>
    [Fact]
    public void WasFilled_PersonMarked_DoesNotMatchAPathOfTheSameText()
    {
        var name = "Person " + Guid.NewGuid().ToString("N");
        ProviderFills.MarkPerson(name);

        var asPath = ProviderFills.WasFilled(name, null, DateTime.UtcNow);
        var asPerson = ProviderFills.WasFilled(null, name, DateTime.UtcNow);

        Assert.False(asPath);
        Assert.True(asPerson);
    }

    /// <summary>
    /// A fill is still remembered shortly before its time runs out.
    /// True: a save event a few minutes after the fill is still recognised.
    /// False: slow saves are announced as edits made here.
    /// </summary>
    [Fact]
    public void WasFilled_WithinTheRememberedTime_IsTrue()
    {
        var path = "/local/movies/" + Guid.NewGuid().ToString("N") + ".mkv";
        ProviderFills.MarkPath(path);

        var found = ProviderFills.WasFilled(path, null, DateTime.UtcNow + ProviderFills.Remembered - TimeSpan.FromMinutes(1));

        Assert.True(found);
    }

    /// <summary>
    /// Neither a path nor a name gives nothing to match.
    /// True: an object with no identity is never taken as filled.
    /// False: the check throws inside Jellyfin's save event.
    /// </summary>
    [Fact]
    public void WasFilled_NoPathAndNoName_IsFalse()
    {
        var found = ProviderFills.WasFilled(null, null, DateTime.UtcNow);

        Assert.False(found);
    }

    /// <summary>
    /// Clearing forgets every fill.
    /// True: a reset leaves no stale fill to hide a real edit.
    /// False: edits made right after a reset are not announced.
    /// </summary>
    [Fact]
    public void Clear_WithFills_ForgetsThemAll()
    {
        var path = "/local/movies/" + Guid.NewGuid().ToString("N") + ".mkv";
        var person = "Person " + Guid.NewGuid().ToString("N");
        ProviderFills.MarkPath(path);
        ProviderFills.MarkPerson(person);

        ProviderFills.Clear();

        Assert.False(ProviderFills.WasFilled(path, person, DateTime.UtcNow));
    }
}
