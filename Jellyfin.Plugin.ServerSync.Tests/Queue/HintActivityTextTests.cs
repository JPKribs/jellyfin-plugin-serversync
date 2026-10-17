using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class HintActivityTextTests
{
    [Theory]
    [InlineData("the peer refused this server's key", "The peer refused this server's key")]
    [InlineData("  already Capital", "Already Capital")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Sentence_StartsWithACapital(string? text, string? expected)
    {
        Assert.Equal(expected, HintActivityLog.Sentence(text));
    }

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

    [Fact]
    public void Changes_ReadsNaturallyForOneOrTwo()
    {
        var one = new List<OutboundHint> { new() { Kind = HintKind.People, Key = "Jane Doe", UserName = "Jane Doe" } };
        var two = new List<OutboundHint>(one) { new() { Kind = HintKind.Content, Key = "x", ItemPath = "/media/Movies/Only A (2021)/Only A (2021).mp4" } };

        Assert.Equal("Person Jane Doe", HintActivityLog.Changes(one));
        Assert.Equal("Person Jane Doe and File Only A (2021)", HintActivityLog.Changes(two));
    }
}
