using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The activity log turns hint rows into short sentences an operator can read.
/// </summary>
public class HintActivityTextTests
{
    /// <summary>
    /// A sentence starts with a capital letter and null or empty text passes through.
    /// True: reasons read as sentences in the activity log.
    /// False: log entries would start in lowercase, or empty text would throw.
    /// </summary>
    [Theory]
    [InlineData("the peer refused this server's key", "The peer refused this server's key")]
    [InlineData("  already Capital", "Already Capital")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Sentence_StartsWithACapital(string? text, string? expected)
    {
        Assert.Equal(expected, ActivityText.Sentence(text));
    }

    /// <summary>
    /// A change summary names each object once, up to a few, and counts the rest.
    /// True: seven objects with one repeated read as the first five names and 'and 2 more'.
    /// False: a repeated object would be listed twice, or a busy sync would list every object.
    /// </summary>
    [Fact]
    public void Changes_NamesEachObjectOnce_AndCountsTheRest()
    {
        var rows = new List<OutboundHint>();
        for (var i = 1; i <= 7; i++)
        {
            rows.Add(new OutboundHint { Kind = HintKind.Metadata, Key = "k" + i, ItemPath = $"/media/Movies/Film {i} (2020)/Film {i} (2020).mkv" });
        }

        rows.Add(new OutboundHint { Kind = HintKind.Metadata, Key = "k1", ItemPath = "/media/Movies/Film 1 (2020)/Film 1 (2020).mkv" });

        var text = HintActivityLog.Changes(rows);

        Assert.StartsWith("Metadata for Film 1 (2020), Metadata for Film 2 (2020)", text, System.StringComparison.Ordinal);
        Assert.EndsWith("and 2 more", text, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// One change or two reads as a plain phrase.
    /// True: a person reads as 'Person Jane Doe', and two changes are joined with 'and'.
    /// False: small summaries would carry list punctuation or the wrong kind name.
    /// </summary>
    [Fact]
    public void Changes_ReadsNaturallyForOneOrTwo()
    {
        var one = new List<OutboundHint> { new() { Kind = HintKind.People, Key = "Jane Doe", UserName = "Jane Doe" } };
        var two = new List<OutboundHint>(one) { new() { Kind = HintKind.Content, Key = "x", ItemPath = "/media/Movies/Only A (2021)/Only A (2021).mp4" } };

        Assert.Equal("Person Jane Doe", HintActivityLog.Changes(one));
        Assert.Equal("Person Jane Doe and File Only A (2021)", HintActivityLog.Changes(two));
    }

    /// <summary>
    /// Summaries read the same way every time: a title case name with the count, and the servers
    /// listed with their counts, the largest first.
    /// True: a busy sync leaves one tidy entry an operator can read at a glance.
    /// False: names would differ in shape, or servers be listed in no useful order.
    /// </summary>
    [Fact]
    public void Summaries_HaveOneShape()
    {
        Assert.Equal("1 Item Synced", ActivityText.SummaryName(1, "Synced"));
        Assert.Equal("12 Items Not Synced", ActivityText.SummaryName(12, "Not Synced"));
        Assert.Equal("1 change", ActivityText.Count(1, "change"));
        Assert.Equal("3 items", ActivityText.Count(3, "item"));
        Assert.Equal("source-a (9), source-b (3), source-c (3)",
            ActivityText.CountedList(new Dictionary<string, int> { ["source-c"] = 3, ["source-a"] = 9, ["source-b"] = 3 }));
        Assert.Equal("filtered out", HintActivityLog.ReasonOf(HintCode.Filtered));
    }
}
