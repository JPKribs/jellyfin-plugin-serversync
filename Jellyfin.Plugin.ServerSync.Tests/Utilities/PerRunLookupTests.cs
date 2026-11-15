using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The answers a run remembers. Too few and a run repeats a costly lookup for every item, too many and
/// an answer outlives the run that asked.
/// </summary>
public class PerRunLookupTests
{
    /// <summary>
    /// A key asked about twice runs the lookup once.
    /// True: the second ask is answered from memory.
    /// False: a run over thousands of items would repeat the lookup for each one.
    /// </summary>
    [Fact]
    public void Get_SameKeyTwice_LooksUpOnce()
    {
        var lookup = new PerRunLookup<string, string?>();
        var calls = 0;

        var first = lookup.Get("user", _ => { calls++; return "found"; });
        var second = lookup.Get("user", _ => { calls++; return "other"; });

        Assert.Equal(1, calls);
        Assert.Equal("found", first);
        Assert.Equal("found", second);
    }

    /// <summary>
    /// A lookup that found nothing is remembered too.
    /// True: a user that is gone is asked about once per run.
    /// False: every item of a missing user would repeat the lookup and its warning.
    /// </summary>
    [Fact]
    public void Get_NoAnswer_IsRemembered()
    {
        var lookup = new PerRunLookup<string, string?>();
        var calls = 0;

        Assert.Null(lookup.Get("gone", _ => { calls++; return null; }));
        Assert.Null(lookup.Get("gone", _ => { calls++; return "late"; }));

        Assert.Equal(1, calls);
    }

    /// <summary>
    /// Different keys are answered on their own.
    /// True: each key runs its own lookup.
    /// False: one user's answer would be handed to another.
    /// </summary>
    [Fact]
    public void Get_DifferentKeys_AreSeparate()
    {
        var lookup = new PerRunLookup<string, string?>();

        Assert.Equal("a!", lookup.Get("a", k => k + "!"));
        Assert.Equal("b!", lookup.Get("b", k => k + "!"));
    }

    /// <summary>
    /// Clearing forgets every answer.
    /// True: the next run asks again and sees a change made in between.
    /// False: a user added or removed between runs would never be noticed.
    /// </summary>
    [Fact]
    public void Clear_ForgetsAnswers()
    {
        var lookup = new PerRunLookup<string, string?>();
        var calls = 0;
        lookup.Get("user", _ => { calls++; return "old"; });

        lookup.Clear();
        var answer = lookup.Get("user", _ => { calls++; return "new"; });

        Assert.Equal(2, calls);
        Assert.Equal("new", answer);
    }

    /// <summary>
    /// Threads that ask for the same key at the same moment share one lookup.
    /// True: the lookup runs once however many workers ask.
    /// False: a parallel refresh would repeat the lookup and log its warning once per worker.
    /// </summary>
    [Fact]
    public async Task Get_ConcurrentAsks_LookUpOnce()
    {
        var lookup = new PerRunLookup<string, string?>();
        var calls = 0;
        using var start = new ManualResetEventSlim(false);

        var tasks = new List<Task<string?>>();
        for (var i = 0; i < 16; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                start.Wait();
                return lookup.Get("user", _ => { Interlocked.Increment(ref calls); return "found"; });
            }));
        }

        start.Set();
        var answers = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(answers, a => Assert.Equal("found", a));
    }

    /// <summary>
    /// A null lookup is refused.
    /// True: the caller learns at once that there is nothing to ask.
    /// False: the failure would surface later inside the dictionary.
    /// </summary>
    [Fact]
    public void Get_NullLookup_Throws()
    {
        var lookup = new PerRunLookup<string, string?>();

        Assert.Throws<ArgumentNullException>(() => lookup.Get("user", null!));
    }
}
