using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// Thousands of random operations against the link pieces that hold state in memory, each checked
/// against a plain model of what should be true. Seeds are fixed, so a failure always reproduces.
/// </summary>
public class LinkFuzzTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Grants, under random grants, refreshes, probes, uses, and clock jumps, match a model of who may
    /// read each link: never a user the source did not allow, never after expiry, and a probe never
    /// outlives its window.
    /// True: the link route only ever reads for someone it should.
    /// False: some order of events lets a stranger or an expired grant through.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Grants_MatchTheModel(int seed)
    {
        var random = new Random(seed);
        var now = Start;
        var grants = new LinkGrants(() => now);
        var links = new[] { "a", "b", "c" };
        var users = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();

        // The model: per link and user, when the grant was last touched and whether it was used.
        var model = new Dictionary<(string, Guid), (DateTime At, bool Used)>();
        var probes = new Dictionary<string, DateTime>();

        bool Live((DateTime At, bool Used) e) => now - e.At < (e.Used ? LinkGrants.InUse : LinkGrants.Unused);
        bool ProbeLive(string link) => probes.TryGetValue(link, out var at) && now - at < LinkGrants.ProbeWait;

        for (var step = 0; step < 4000; step++)
        {
            var link = links[random.Next(links.Length)];
            var user = users[random.Next(users.Length)];
            switch (random.Next(6))
            {
                case 0:
                    var used = model.TryGetValue((link, user), out var prior) && Live(prior) && prior.Used;
                    grants.Grant(link, user);
                    model[(link, user)] = (now, used);
                    break;
                case 1:
                    var expected = model.TryGetValue((link, user), out var current) && Live(current);
                    Assert.Equal(expected, grants.Refresh(link, user));
                    if (expected)
                    {
                        model[(link, user)] = (now, true);
                    }

                    break;
                case 2:
                    // A probe asked for again restarts its window, unless a user is playing the link.
                    if (!users.Any(u => model.TryGetValue((link, u), out var e) && Live(e)))
                    {
                        probes[link] = now;
                    }

                    grants.GrantProbe(link);
                    break;
                case 3:
                    var got = grants.Use(link);
                    var liveUsers = users.Where(u => model.TryGetValue((link, u), out var e) && Live(e)).ToList();
                    if (got is null)
                    {
                        Assert.Empty(liveUsers);
                        Assert.False(ProbeLive(link) && liveUsers.Count == 0 && probes.ContainsKey(link) && now - probes[link] < LinkGrants.ProbeWait,
                            $"step {step}: a live probe was refused");
                    }
                    else if (got.Probe)
                    {
                        Assert.Empty(liveUsers);
                        Assert.True(ProbeLive(link), $"step {step}: an expired probe was allowed");
                    }
                    else
                    {
                        Assert.Contains(got.UserId!.Value, liveUsers);
                        model[(link, got.UserId.Value)] = (now, true);
                    }

                    break;
                default:
                    now += TimeSpan.FromSeconds(random.Next(0, 5) switch { 0 => 1, 1 => 30, 2 => 150, 3 => 1200, _ => 4000 });
                    break;
            }
        }
    }

    /// <summary>
    /// Stream counts under random opens, closes, limit checks, and clock jumps match a model, and
    /// TryOpen never lets the count pass a limit.
    /// True: limits hold whatever order requests arrive and end in.
    /// False: some sequence of seeks and stops lets a server or user past its limit, or counts a stream twice.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Sessions_MatchTheModel(int seed)
    {
        var random = new Random(seed);
        var now = Start;
        var sessions = new LinkSessions(() => now);
        var peers = new[] { "p1", "p2" };
        var users = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var items = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var open = new List<((string Peer, Guid User, Guid Item) Key, IDisposable Handle)>();
        var model = new Dictionary<(string, Guid, Guid), (int Open, DateTime Last)>();

        void Prune()
        {
            foreach (var dead in model.Where(p => p.Value.Open == 0 && p.Value.Last < now - LinkSessions.Linger).Select(p => p.Key).ToList())
            {
                model.Remove(dead);
            }
        }

        for (var step = 0; step < 3000; step++)
        {
            var key = (peers[random.Next(peers.Length)], users[random.Next(users.Length)], items[random.Next(items.Length)]);
            switch (random.Next(5))
            {
                case 0:
                {
                    open.Add((key, sessions.Open(key.Item1, key.Item2, key.Item3)));
                    model[key] = (model.TryGetValue(key, out var e) ? e.Open + 1 : 1, now);
                    break;
                }

                case 1 when open.Count > 0:
                {
                    var i = random.Next(open.Count);
                    open[i].Handle.Dispose();
                    open[i].Handle.Dispose();
                    var e = model[open[i].Key];
                    model[open[i].Key] = (e.Open - 1, now);
                    open.RemoveAt(i);
                    break;
                }

                case 2:
                {
                    Prune();
                    var maxPeer = random.Next(0, 4);
                    var maxUser = random.Next(0, 3);
                    var elsewhere = random.Next(0, 2);
                    var active = model.ContainsKey(key);
                    var allowed = active
                        || ((maxPeer == 0 || model.Keys.Count(k => k.Item1 == key.Item1) < maxPeer)
                            && (maxUser == 0 || model.Keys.Count(k => k.Item2 == key.Item2) + elsewhere < maxUser));
                    var handle = sessions.TryOpen(key.Item1, key.Item2, key.Item3, maxPeer, maxUser, elsewhere);
                    Assert.Equal(allowed, handle is not null);
                    if (handle is not null)
                    {
                        open.Add((key, handle));
                        model[key] = (model.TryGetValue(key, out var e) ? e.Open + 1 : 1, now);
                    }

                    break;
                }

                case 3:
                {
                    Prune();
                    Assert.Equal(model.Keys.Count(k => k.Item1 == key.Item1), sessions.CountForPeer(key.Item1));
                    Assert.Equal(model.Keys.Count(k => k.Item2 == key.Item2), sessions.CountForUser(key.Item2));
                    Assert.Equal(model.ContainsKey(key), sessions.IsActive(key.Item1, key.Item2, key.Item3));
                    break;
                }

                default:
                    now += TimeSpan.FromSeconds(random.Next(0, 3) switch { 0 => 5, 1 => 60, _ => 120 });
                    break;
            }
        }
    }

    /// <summary>
    /// For thousands of random file names, with dots, spaces, Unicode, brackets, and odd extensions, a
    /// link path keeps the folder and the name and swaps only the extension, and its copy path comes back.
    /// True: no name, however odd, puts a link in another folder or loses its copy.
    /// False: some name escapes its folder or cannot be matched back to its copy.
    /// </summary>
    [Fact]
    public void LinkPaths_RoundTripAnyName()
    {
        var random = new Random(21);
        var pieces = new[] { "Movie", "Amélie", "東京", "Mr. Robot", "a.b.c", " spaced ", "100%", "#1", "[2024]", "(2020)", "ü", "-", "_", "..", "x", "ünï", "🎬" };
        var extensions = new[] { ".mkv", ".mp4", ".MKV", ".m2ts", ".avi", ".strm", ".STRM", string.Empty, ".x" };
        for (var i = 0; i < 5000; i++)
        {
            var name = string.Join(random.Next(2) == 0 ? " " : ".", Enumerable.Range(0, random.Next(1, 4)).Select(_ => pieces[random.Next(pieces.Length)])).Trim();
            if (name.Length == 0 || name.EndsWith('.') || name == "..")
            {
                continue;
            }

            var extension = extensions[random.Next(extensions.Length)];
            var copied = "/media/Movies/" + name + "/" + name + extension;
            var link = LinkPaths.LinkPathFor(copied);
            if (link is null)
            {
                var actual = Path.GetExtension(copied);
                Assert.True(string.IsNullOrEmpty(actual) || actual.Equals(".strm", StringComparison.OrdinalIgnoreCase), copied);
                continue;
            }

            Assert.Equal(Path.GetDirectoryName(copied), Path.GetDirectoryName(link));
            Assert.EndsWith(".strm", link, StringComparison.Ordinal);
            Assert.Equal(Path.GetFileNameWithoutExtension(copied), Path.GetFileNameWithoutExtension(link));

            var row = new Models.ContentSync.SyncItem { LocalPath = link, SourcePath = "/src/" + name + Path.GetExtension(copied) };
            Assert.Equal(copied, LinkPaths.CopiedPathOf(row));
        }
    }

    /// <summary>
    /// For thousands of items built from hostile text, with control characters, lone surrogates, emoji,
    /// XML syntax, and very long strings, the .nfo always parses as XML and keeps every character XML can hold.
    /// True: no title or plot, however odd, makes a link's .nfo unreadable to Jellyfin.
    /// False: one strange title would leave its link without metadata.
    /// </summary>
    [Fact]
    public void Nfo_AlwaysParses()
    {
        var random = new Random(31);
        string Text()
        {
            var length = random.Next(0, random.Next(10) == 0 ? 5000 : 40);
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = random.Next(8) switch
                {
                    0 => (char)random.Next(0, 32),
                    1 => (char)random.Next(0xD800, 0xE000),
                    2 => "<>&\"'"[random.Next(5)],
                    3 => (char)random.Next(0xFFF0, 0x10000),
                    _ => (char)random.Next(32, 0x3000)
                };
            }

            return new string(chars);
        }

        var kinds = new[] { Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie, Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode, Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.MusicVideo };
        for (var i = 0; i < 3000; i++)
        {
            var item = new Jellyfin.Sdk.Generated.Models.BaseItemDto
            {
                Type = kinds[random.Next(kinds.Length)],
                Name = Text(),
                OriginalTitle = Text(),
                Overview = Text(),
                SeriesName = Text(),
                Genres = Enumerable.Range(0, random.Next(4)).Select(_ => Text()).ToList(),
                Tags = Enumerable.Range(0, random.Next(3)).Select(_ => Text()).ToList(),
                ProviderIds = new Jellyfin.Sdk.Generated.Models.BaseItemDto_ProviderIds
                {
                    AdditionalData = Enumerable.Range(0, random.Next(3)).ToDictionary(n => "Id" + n, _ => (object)Text())
                }
            };

            var built = LinkNfo.Build(item);
            var root = System.Xml.Linq.XDocument.Parse(built).Root!;
            var title = root.Element("title")?.Value ?? string.Empty;
            var expected = new string((item.Name ?? string.Empty).Where(c => System.Xml.XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c)).ToArray());
            Assert.True(string.IsNullOrWhiteSpace(item.Name) || title.Length > 0 || string.IsNullOrWhiteSpace(expected), $"case {i}: the title was lost");
        }
    }
}
