using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Several servers wired into a ring or mesh at the decision level. Each one has its own real stamp store, outbound queue,
/// triage, publisher, resolver, and apply guard. The handlers' Jellyfin writes are modelled as plain values, and their
/// stamp decisions follow the handlers line for line, so a hint or a scan between two servers decides as the plugin does.
/// </summary>
public sealed class RingNetwork : IDisposable
{
    /// <summary>The library root every server uses, so a path means the same file everywhere.</summary>
    public const string Root = "/media/movies";

    /// <summary>The one user every server maps to itself.</summary>
    public static readonly Guid User = Guid.Parse("44444444444444444444444444444444");

    private readonly Dictionary<string, RingServer> _servers = new(StringComparer.Ordinal);
    private DateTime _clock;

    private RingNetwork(IEnumerable<string> names)
    {
        // Stamps are bounded to the present, so the simulated clock runs in the past.
        var start = DateTime.UtcNow.AddHours(-6);
        _clock = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second, DateTimeKind.Utc);
        var index = 0;
        foreach (var name in names)
        {
            index++;
            _servers[name] = new RingServer(name, ServerIdFor(index));
        }
    }

    /// <summary>Gets every server, in the order they were named.</summary>
    public IReadOnlyList<RingServer> Servers => _servers.Values.ToList();

    /// <summary>Gets how many hints were delivered since the network was built.</summary>
    public int Delivered { get; private set; }

    /// <summary>Gets how many values were written by a hint or a scan since the network was built.</summary>
    public int Writes { get; private set; }

    /// <summary>Gets the server with a name.</summary>
    /// <param name="name">The server's name.</param>
    public RingServer this[string name] => _servers[name];

    /// <summary>Builds servers with no links yet.</summary>
    /// <param name="names">The server names.</param>
    /// <returns>The network.</returns>
    public static RingNetwork Create(params string[] names) => new(names);

    /// <summary>Builds a one-way ring, where each server sends to the next and the last to the first.</summary>
    /// <param name="names">The server names in ring order.</param>
    /// <returns>The network.</returns>
    public static RingNetwork Ring(params string[] names)
    {
        var net = Create(names);
        for (var i = 0; i < names.Length; i++)
        {
            net.Link(names[i], names[(i + 1) % names.Length]);
        }

        return net;
    }

    /// <summary>Builds a two-way ring, where each server syncs with both neighbours.</summary>
    /// <param name="names">The server names in ring order.</param>
    /// <returns>The network.</returns>
    public static RingNetwork BiRing(params string[] names)
    {
        var net = Create(names);
        for (var i = 0; i < names.Length; i++)
        {
            net.Link(names[i], names[(i + 1) % names.Length]);
            net.Link(names[(i + 1) % names.Length], names[i]);
        }

        return net;
    }

    /// <summary>Builds a full mesh, where every server syncs with every other.</summary>
    /// <param name="names">The server names.</param>
    /// <returns>The network.</returns>
    public static RingNetwork Mesh(params string[] names)
    {
        var net = Create(names);
        foreach (var a in names)
        {
            foreach (var b in names.Where(b => b != a))
            {
                net.Link(a, b);
            }
        }

        return net;
    }

    /// <summary>Builds a named topology.</summary>
    /// <param name="topology">ring3, ring4, biring4, mesh3, or mesh4.</param>
    /// <returns>The network.</returns>
    public static RingNetwork Build(string topology) => topology switch
    {
        "ring3" => Ring("A", "B", "C"),
        "ring4" => Ring("A", "B", "C", "D"),
        "biring4" => BiRing("A", "B", "C", "D"),
        "mesh3" => Mesh("A", "B", "C"),
        "mesh4" => Mesh("A", "B", "C", "D"),
        _ => throw new ArgumentOutOfRangeException(nameof(topology), topology, null)
    };

    /// <summary>
    /// Makes one server send hints to another and the other scan it, as a Push or Sync entry on the sender and a Pull or Sync entry on the receiver.
    /// A link both ways makes both entries Sync.
    /// </summary>
    /// <param name="from">The sender.</param>
    /// <param name="to">The receiver.</param>
    public void Link(string from, string to)
    {
        var sender = _servers[from];
        var receiver = _servers[to];
        sender.SetEntry(receiver, sends: true);
        receiver.SetEntry(sender, sends: false);
    }

    /// <summary>Moves the simulated clock on and returns the new time.</summary>
    /// <param name="seconds">How far to move it.</param>
    /// <returns>The new time.</returns>
    public DateTime Tick(int seconds = 1)
    {
        _clock = _clock.AddSeconds(seconds);
        return _clock;
    }

    /// <summary>Gets the simulated time.</summary>
    public DateTime Now => _clock;

    /// <summary>Adds the same media file on every server, each with its own item id, holding one starting value.</summary>
    /// <param name="file">The file name under the root.</param>
    /// <param name="value">The starting metadata value.</param>
    /// <returns>The path.</returns>
    public string AddItem(string file = "film.mkv", string value = "v0")
    {
        var path = Root + "/" + file;
        foreach (var server in _servers.Values)
        {
            server.AddItem(path, value);
        }

        return path;
    }

    // ---------------------------------------------------------------- metadata

    /// <summary>Makes a hand edit on a server and raises its hint at once, as the observer does once Send Changes After ends.</summary>
    /// <param name="server">The server edited on.</param>
    /// <param name="path">The file.</param>
    /// <param name="value">The new value.</param>
    /// <param name="at">The edit time, or the next tick.</param>
    /// <returns>The stamp the edit carries.</returns>
    public ObjectVersion Edit(string server, string path, string value, DateTime? at = null)
    {
        var pending = Note(server, path, value, at);
        Raise(pending);
        return pending.Version;
    }

    /// <summary>Makes a hand edit and stamps it, but leaves its hint gathering until <see cref="Raise"/>.</summary>
    /// <param name="server">The server edited on.</param>
    /// <param name="path">The file.</param>
    /// <param name="value">The new value.</param>
    /// <param name="at">The edit time, or the next tick.</param>
    /// <returns>The gathering change.</returns>
    public PendingEdit Note(string server, string path, string value, DateTime? at = null)
    {
        var s = _servers[server];
        var time = at ?? Tick();
        s.Values[path] = value;
        var key = s.MetadataKey(path);
        var version = new ObjectVersion { Kind = HintKind.Metadata, Key = key, ServerId = s.Id, Timestamp = time };
        s.Versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = key, ServerId = s.Id, Timestamp = time });
        return new PendingEdit(s, path, version, Recorded: true);
    }

    /// <summary>Makes a provider's change on a server, which is announced unrecorded and leaves no stamp.</summary>
    /// <param name="server">The server.</param>
    /// <param name="path">The file.</param>
    /// <param name="value">The value the provider wrote.</param>
    /// <param name="announce">Whether the observer announces it.</param>
    public void ProviderChange(string server, string path, string value, bool announce = true)
    {
        var s = _servers[server];
        s.Values[path] = value;
        if (announce)
        {
            Raise(new PendingEdit(s, path, new ObjectVersion { ServerId = s.Id, Timestamp = Tick() }, Recorded: false));
        }
    }

    /// <summary>Raises a gathered change, skipping it when a peer's newer edit applied during the wait, as LocalChangeObserver.Raise does.</summary>
    /// <param name="pending">The gathered change.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int Raise(PendingEdit pending)
    {
        var s = pending.Server;
        var key = s.MetadataKey(pending.Path);
        var version = new ObjectVersion { ServerId = s.Id, Timestamp = pending.Version.Timestamp };
        if (pending.Recorded
            && s.Versions.Get(HintKind.Metadata, key) is { } current
            && !string.Equals(current.ServerId, s.Id, StringComparison.OrdinalIgnoreCase)
            && VersionDecider.IsNewer(current, new ObjectVersion { Kind = HintKind.Metadata, Key = key, ServerId = s.Id, Timestamp = version.Timestamp }))
        {
            return 0;
        }

        return s.Publisher.PublishMetadata(s.ItemId(pending.Path), pending.Path, version, excludePeerKey: null, recorded: pending.Recorded, itemType: "Movie");
    }

    // ---------------------------------------------------------------- delivery

    /// <summary>Lists the hints waiting to be sent on every server, oldest first.</summary>
    /// <returns>The waiting rows with their sender.</returns>
    public IReadOnlyList<(RingServer From, OutboundHint Row)> Waiting()
        => _servers.Values.SelectMany(s => s.Outbound.GetAll().Select(r => (From: s, Row: r))).OrderBy(p => p.Row.CreatedAt).ThenBy(p => p.Row.Id).ToList();

    /// <summary>Delivers every waiting hint, and those they cause, until none is left.</summary>
    /// <param name="order">fifo, lifo, or a seed for a random order.</param>
    /// <param name="max">The most deliveries before the network is called looping.</param>
    /// <returns>How many hints were delivered.</returns>
    public int DeliverAll(string order = "fifo", int max = 500)
    {
        var random = int.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) ? new Random(seed) : null;
        var count = 0;
        while (true)
        {
            var waiting = Waiting();
            if (waiting.Count == 0)
            {
                return count;
            }

            if (count >= max)
            {
                throw new InvalidOperationException($"Still {waiting.Count} hint(s) waiting after {max} deliveries, so the network loops");
            }

            var next = random is not null ? waiting[random.Next(waiting.Count)] : order == "lifo" ? waiting[^1] : waiting[0];
            Deliver(next.From, next.Row);
            count++;
        }
    }

    /// <summary>Delivers the one waiting hint from a sender to a receiver.</summary>
    /// <param name="from">The sender.</param>
    /// <param name="to">The receiver.</param>
    /// <returns>The code the receiver answered.</returns>
    public HintCode DeliverOne(string from, string to)
    {
        var row = _servers[from].Outbound.GetAll().Single(r => r.PeerKey == to);
        return Deliver(_servers[from], row);
    }

    /// <summary>Drops a waiting hint unsent, as when it expires or its peer stays down until a scan carries it.</summary>
    /// <param name="from">The sender.</param>
    /// <param name="to">The receiver.</param>
    public void Lose(string from, string to)
    {
        foreach (var row in _servers[from].Outbound.GetAll().Where(r => r.PeerKey == to))
        {
            _servers[from].Outbound.Delete(row.Id);
        }
    }

    /// <summary>Delivers one hint: triage, then the handler of its kind.</summary>
    private HintCode Deliver(RingServer from, OutboundHint row)
    {
        from.Outbound.Delete(row.Id);
        Delivered++;
        var to = _servers[row.PeerKey];
        var origin = to.Config.Configuration.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, from.Id, StringComparison.OrdinalIgnoreCase));
        if (origin is null)
        {
            return HintCode.NotMapped;
        }

        var hint = row.ToHint(from.Id);
        var assessment = to.Triage.Assess(origin, hint, from.Id, DateTime.UtcNow);
        to.Answers.Add((from.Name, hint.Kind, assessment.Code));
        if (assessment.Code != HintCode.Queued)
        {
            return assessment.Code;
        }

        var inbound = InboundHint.FromHint(hint, DateTime.UtcNow);
        var code = hint.Kind switch
        {
            HintKind.Metadata => ApplyMetadata(inbound, origin, from, to),
            HintKind.Playlists => ApplyPlaylist(inbound, origin, from, to),
            HintKind.History => ApplyHistory(inbound, origin, from, to),
            HintKind.Content => ApplyContent(inbound, origin, from, to),
            _ => HintCode.Unsupported
        };
        to.Answers[^1] = (from.Name, hint.Kind, code);
        return code;
    }

    /// <summary>Follows ItemHintHandler.SettleAndApplyAsync: the value is read live from the origin, and the stamps decide.</summary>
    private HintCode ApplyMetadata(InboundHint hint, SourceServer origin, RingServer from, RingServer to)
    {
        var path = hint.ItemPath!;
        var localKey = to.MetadataKey(path);
        var incoming = ItemHintHandler.CurrentVersion(HintProtocol.IncomingVersion(hint, HintKind.Metadata, localKey), from.Stamp(path), hint.Recorded);
        var originValue = from.Values[path];

        // A row that is not queued already matches, so the hint's stamp applies here.
        if (string.Equals(to.Values[path], originValue, StringComparison.Ordinal))
        {
            if (hint.Recorded)
            {
                to.Resolver.Record(incoming);
            }

            return HintCode.Applied;
        }

        var local = to.Resolver.Recorded(HintKind.Metadata, localKey);
        if (local is not null && (!hint.Recorded || VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep))
        {
            if (to.Resolver.IsThisServer(local))
            {
                to.Publisher.PublishMetadata(to.ItemId(path), path, local, excludePeerKey: null, itemType: hint.ItemType);
            }

            return HintCode.Superseded;
        }

        using (to.Guard.Enter(HintProtocol.GuardKey(HintKind.Metadata, localKey)))
        {
            to.Values[path] = originValue;
            Writes++;
        }

        if (hint.Recorded)
        {
            to.Resolver.Record(incoming);
        }

        to.AfterApply?.Invoke(to, path);
        return HintCode.Applied;
    }

    // ---------------------------------------------------------------- scans

    /// <summary>
    /// Runs one server's scheduled metadata scan against each server it pulls from, one after another, as the refresh,
    /// ScanConflictSettler, and AppliedVersionRecorder do: a differing value is settled by stamps, a kept value made here is
    /// announced to a server it sends to, and an applied value records the stamp read with it.
    /// </summary>
    /// <param name="server">The scanning server.</param>
    /// <returns>How many values were written.</returns>
    public int Scan(string server)
    {
        var x = _servers[server];
        var written = 0;
        foreach (var entry in x.Config.Configuration.Servers.Where(s => s.Pulls).ToList())
        {
            var y = _servers[entry.Key];
            foreach (var path in x.Values.Keys.ToList())
            {
                written += ScanOne(x, entry, path, y.Values[path], y.Versions.Get(HintKind.Metadata, y.MetadataKey(path)));
            }
        }

        return written;
    }

    /// <summary>Runs every server's scan once, one server after another.</summary>
    /// <returns>How many values were written.</returns>
    public int ScanAll() => _servers.Keys.Sum(Scan);

    /// <summary>
    /// Runs every server's scan at the same moment: each reads its peers' values and stamps before any of them writes,
    /// which is what overlapping scheduled scans on several servers do.
    /// </summary>
    /// <returns>How many values were written.</returns>
    public int ScanAllAtOnce()
    {
        var snapshot = _servers.Values.ToDictionary(
            s => s.Name,
            s => s.Values.Keys.ToDictionary(p => p, p => (Value: s.Values[p], Version: s.Versions.Get(HintKind.Metadata, s.MetadataKey(p)))));
        var written = 0;
        foreach (var x in _servers.Values)
        {
            foreach (var entry in x.Config.Configuration.Servers.Where(s => s.Pulls).ToList())
            {
                foreach (var path in x.Values.Keys.ToList())
                {
                    var read = snapshot[entry.Key][path];
                    written += ScanOne(x, entry, path, read.Value, read.Version);
                }
            }
        }

        return written;
    }

    /// <summary>Settles one row a scan read from a peer.</summary>
    private int ScanOne(RingServer x, SourceServer entry, string path, string peerValue, ObjectVersion? peerVersion)
    {
        // A row whose values match is not queued.
        if (string.Equals(x.Values[path], peerValue, StringComparison.Ordinal))
        {
            return 0;
        }

        var localKey = x.MetadataKey(path);
        var decision = x.Resolver.Decide(HintKind.Metadata, localKey, peerVersion is null ? null : new ObjectVersion { Kind = HintKind.Metadata, Key = localKey, ServerId = peerVersion.ServerId, Timestamp = peerVersion.Timestamp }, entry.ServerId);
        if (decision == VersionDecision.Keep)
        {
            x.Resolver.ClearPending(HintKind.Metadata, localKey);
            var version = x.Resolver.Recorded(HintKind.Metadata, localKey);
            if (entry.Pushes && version is not null && x.Resolver.IsThisServer(version))
            {
                x.Publisher.PublishMetadata(x.ItemId(path), path, version, excludePeerKey: null);
            }

            return 0;
        }

        if (peerVersion is null)
        {
            x.Resolver.ClearPending(HintKind.Metadata, localKey);
        }
        else
        {
            x.Resolver.RecordPending(new ObjectVersion { Kind = HintKind.Metadata, Key = localKey, ServerId = peerVersion.ServerId, Timestamp = peerVersion.Timestamp });
        }

        using (x.Guard.Enter(HintProtocol.GuardKey(HintKind.Metadata, localKey)))
        {
            x.Values[path] = peerValue;
            Writes++;
        }

        x.Resolver.PromotePending(HintKind.Metadata, localKey);
        return 1;
    }

    /// <summary>Delivers every hint and runs every scan, again and again, until a full pass changes nothing and sends nothing.</summary>
    /// <param name="order">The delivery order.</param>
    /// <param name="maxPasses">The most passes before the network is called flapping.</param>
    /// <returns>How many passes it took.</returns>
    public int Settle(string order = "fifo", int maxPasses = 20)
    {
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            var delivered = DeliverAll(order);
            var written = ScanAll();
            if (delivered == 0 && written == 0 && Waiting().Count == 0)
            {
                return pass;
            }
        }

        throw new InvalidOperationException($"The network still changed after {maxPasses} passes, so it flaps");
    }

    // ---------------------------------------------------------------- playlists

    /// <summary>Makes a hand edit or creation of a playlist on a server and announces it.</summary>
    /// <param name="server">The server.</param>
    /// <param name="rootKey">The playlist key shared by the original and its copies.</param>
    /// <param name="value">The playlist's contents.</param>
    /// <param name="at">The edit time, or the next tick.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int EditPlaylist(string server, string rootKey, string value, DateTime? at = null)
    {
        var s = _servers[server];
        var time = at ?? Tick();
        var id = s.PlaylistIds.TryGetValue(rootKey, out var known) ? known : s.PlaylistIds[rootKey] = Guid.NewGuid();
        s.Playlists[rootKey] = value;
        s.Versions.Set(new ObjectVersion { Kind = HintKind.Playlists, Key = rootKey, ServerId = s.Id, Timestamp = time });
        var version = new ObjectVersion { ServerId = s.Id, Timestamp = time };
        if (s.Versions.Get(HintKind.Playlists, rootKey) is { } current && !s.Resolver.IsThisServer(current) && VersionDecider.IsNewer(current, new ObjectVersion { ServerId = s.Id, Timestamp = time }))
        {
            return 0;
        }

        return s.Publisher.PublishPlaylist(id, rootKey, "List", User, version, excludePeerKey: null, recorded: true, deleted: false, copy: false);
    }

    /// <summary>Deletes a playlist by hand on a server and announces the deletion, as LocalChangeObserver.OnItemRemoved does.</summary>
    /// <param name="server">The server.</param>
    /// <param name="rootKey">The playlist key.</param>
    /// <param name="at">The deletion time, or the next tick.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int DeletePlaylist(string server, string rootKey, DateTime? at = null)
    {
        var s = _servers[server];
        var id = s.PlaylistIds.TryGetValue(rootKey, out var known) ? known : Guid.NewGuid();
        s.Playlists.Remove(rootKey);
        var version = new ObjectVersion { ServerId = s.Id, Timestamp = at ?? Tick() };
        return s.Publisher.PublishPlaylist(id, rootKey, "List", User, version, excludePeerKey: null, recorded: true, deleted: true, copy: false);
    }

    /// <summary>Follows PlaylistHintHandler.ApplyLockedAsync for a Sync origin.</summary>
    private HintCode ApplyPlaylist(InboundHint hint, SourceServer origin, RingServer from, RingServer to)
    {
        var rootKey = hint.Key;
        var incoming = HintProtocol.IncomingVersion(hint, HintKind.Playlists, rootKey);
        if (origin.Mode != ServerMode.Pull
            && to.Resolver.Recorded(HintKind.Playlists, rootKey) is { } local
            && VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep)
        {
            if (to.Resolver.IsThisServer(local) && to.Playlists.ContainsKey(rootKey))
            {
                to.Publisher.PublishPlaylist(to.PlaylistIds[rootKey], rootKey, "List", User, local, excludePeerKey: null, recorded: true, deleted: false, copy: false);
            }

            return HintCode.Superseded;
        }

        if (HintProtocol.IsPlaylistDeletion(hint.ItemType))
        {
            if (to.Playlists.Remove(rootKey))
            {
                Writes++;
            }

            to.Resolver.Record(incoming);
            return HintCode.Applied;
        }

        if (!from.Playlists.TryGetValue(rootKey, out var source))
        {
            return HintCode.SourceGone;
        }

        if (!to.Playlists.TryGetValue(rootKey, out var mine) || mine != source)
        {
            to.PlaylistIds.TryAdd(rootKey, Guid.NewGuid());
            to.Playlists[rootKey] = source;
            Writes++;
        }

        if (hint.Recorded)
        {
            to.Resolver.Record(incoming);
        }

        return HintCode.Applied;
    }

    // ---------------------------------------------------------------- history

    /// <summary>Makes a history change on a server, stamping the whole and the changed parts, and announces it.</summary>
    /// <param name="server">The server.</param>
    /// <param name="path">The file.</param>
    /// <param name="part">The part changed.</param>
    /// <param name="value">The part's new value.</param>
    /// <param name="at">The change time, or the next tick.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int Play(string server, string path, HistoryParts part, string value, DateTime? at = null)
    {
        var s = _servers[server];
        var time = at ?? Tick();
        s.SetHistory(path, part, value);
        var key = s.HistoryKey(path);
        s.Versions.Set(new ObjectVersion { Kind = HintKind.History, Key = key, ServerId = s.Id, Timestamp = time });
        s.Versions.Set(new ObjectVersion { Kind = HintKind.History, Key = HintProtocol.HistoryPartKey(key, part), ServerId = s.Id, Timestamp = time });
        var version = new ObjectVersion { ServerId = s.Id, Timestamp = time };
        return s.Publisher.PublishHistory(User, "viewer", s.ItemId(path), path, version, excludePeerKey: null, itemType: HintProtocol.HistoryItemType("Movie", part));
    }

    /// <summary>
    /// Follows HistoryHintHandler.ApplyAsync on its version path (first contact, or a value taken from a third server):
    /// each part the origin changed is weighed against its own stamp, both sides end on the merged state, and when the merge
    /// moved past the origin the receiver announces it under a new stamp of its own to everyone but the origin.
    /// </summary>
    private HintCode ApplyHistory(InboundHint hint, SourceServer origin, RingServer from, RingServer to)
    {
        var path = hint.ItemPath!;
        var localKey = to.HistoryKey(path);
        var incoming = HintProtocol.IncomingVersion(hint, HintKind.History, localKey);
        HintProtocol.SplitHistoryItemType(hint.ItemType, out var parts);
        var source = from.History[path];
        var local = to.History[path];
        var merged = local;
        var taken = HistoryParts.None;
        var localVersion = to.Versions.Get(HintKind.History, localKey);

        if (localVersion is null)
        {
            merged = source;
        }
        else if (parts != HistoryParts.None)
        {
            foreach (var part in new[] { HistoryParts.Played, HistoryParts.Favorite })
            {
                if (!parts.HasFlag(part))
                {
                    continue;
                }

                var mine = to.Versions.Get(HintKind.History, HintProtocol.HistoryPartKey(localKey, part)) ?? localVersion;
                if (VersionDecider.SameVersion(incoming, mine) || !VersionDecider.IsNewer(incoming, mine))
                {
                    continue;
                }

                taken |= part;
                merged = part == HistoryParts.Played ? merged with { Played = source.Played } : merged with { Favorite = source.Favorite };
            }
        }
        else if (VersionDecider.Decide(localVersion, incoming, local == source) == VersionDecision.Apply)
        {
            merged = source;
        }

        if (merged == local && merged == source)
        {
            to.Versions.Set(incoming);
            return HintCode.Applied;
        }

        // The origin takes the merged state through the negotiation, under its own guard and with no stamp.
        if (from.History[path] != merged)
        {
            from.History[path] = merged;
            Writes++;
        }

        if (to.History[path] != merged)
        {
            to.History[path] = merged;
            Writes++;
        }

        foreach (var part in new[] { HistoryParts.Played, HistoryParts.Favorite })
        {
            if (taken.HasFlag(part))
            {
                to.Versions.Set(new ObjectVersion { Kind = HintKind.History, Key = HintProtocol.HistoryPartKey(localKey, part), ServerId = incoming.ServerId, Timestamp = incoming.Timestamp });
            }
        }

        if (source != merged)
        {
            var version = new ObjectVersion { ServerId = to.Id, Timestamp = Tick() };
            to.Publisher.PublishHistory(User, "viewer", to.ItemId(path), path, version, excludePeerKey: origin.Key, itemType: HintProtocol.HistoryItemType("Movie", taken == HistoryParts.None ? parts : taken));
        }
        else
        {
            to.Versions.Set(incoming);
        }

        return HintCode.Applied;
    }

    // ---------------------------------------------------------------- content

    /// <summary>Adds a new media file on a server by hand and announces it, as the observer does for a file it did not write.</summary>
    /// <param name="server">The server.</param>
    /// <param name="file">The file name under the root.</param>
    /// <returns>The path.</returns>
    public string AddFile(string server, string file)
    {
        var s = _servers[server];
        var path = Root + "/" + file;
        s.Files.Add(path);
        s.FileIds[path] = Guid.NewGuid();
        s.Publisher.PublishContent(s.FileIds[path], path, new ObjectVersion { ServerId = s.Id, Timestamp = Tick() }, excludePeerKey: null, itemType: "Movie");
        return path;
    }

    /// <summary>Removes a media file on a server and announces the removal, as the observer does once the removal hold ends.</summary>
    /// <param name="server">The server.</param>
    /// <param name="path">The file.</param>
    public void RemoveFile(string server, string path)
    {
        var s = _servers[server];
        if (!s.Files.Remove(path))
        {
            return;
        }

        s.Publisher.PublishContent(s.FileIds[path], path, new ObjectVersion { ServerId = s.Id, Timestamp = Tick() }, excludePeerKey: null, itemType: "Movie", removed: true);
    }

    /// <summary>
    /// Follows ContentHintHandler.ApplyAsync: a file the origin no longer holds removes only a copy that came from that origin,
    /// and a file it holds is copied here with a row naming the origin, which the publisher never announces.
    /// A copy that leaves is announced like any removal, as the observer does.
    /// </summary>
    private HintCode ApplyContent(InboundHint hint, SourceServer origin, RingServer from, RingServer to)
    {
        var path = hint.ItemPath!;
        if (!from.Files.Contains(path))
        {
            var held = to.Content.GetByLocalPath(path);
            if (HintProtocol.IsContentRemoval(hint.ItemType) && held is not null && string.Equals(held.ServerKey, origin.Key, StringComparison.OrdinalIgnoreCase) && to.Files.Contains(path))
            {
                to.Content.Delete(held.SourceItemId);
                Writes++;
                RemoveFile(to.Name, path);
            }

            return HintCode.SourceGone;
        }

        if (to.Files.Contains(path))
        {
            return HintCode.Applied;
        }

        to.Content.Upsert(new SyncItem
        {
            SourceItemId = from.FileIds[path].ToString("N", CultureInfo.InvariantCulture),
            SourceLibraryId = "lib",
            LocalLibraryId = "lib",
            SourcePath = path,
            LocalPath = path,
            ServerKey = origin.Key,
            Status = SyncStatus.Synced,
            StatusDate = DateTime.UtcNow
        });
        to.Files.Add(path);
        to.FileIds[path] = Guid.NewGuid();
        Writes++;

        // The arrival raises ItemAdded, which the observer would turn into a content hint, and the publisher drops it.
        to.Publisher.PublishContent(to.FileIds[path], path, new ObjectVersion { ServerId = to.Id, Timestamp = Tick() }, excludePeerKey: null, itemType: "Movie");
        return HintCode.Applied;
    }

    // ---------------------------------------------------------------- checks

    /// <summary>Gets the distinct metadata values the servers hold for a file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The distinct values.</returns>
    public IReadOnlyList<string> ValuesOf(string path) => _servers.Values.Select(s => s.Values[path]).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Gets the distinct metadata stamps the servers hold for a file, as server and time.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The distinct stamps.</returns>
    public IReadOnlyList<string> StampsOf(string path)
        => _servers.Values.Select(s => s.Versions.Get(HintKind.Metadata, s.MetadataKey(path)) is { } v ? v.ServerId + "@" + v.Timestamp.ToString("O", CultureInfo.InvariantCulture) : "none").Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var server in _servers.Values)
        {
            server.Dispose();
        }
    }

    /// <summary>Builds a 32 character server id whose order follows the server's position, so a tie goes to the later server.</summary>
    private static string ServerIdFor(int index) => index.ToString("x2", CultureInfo.InvariantCulture) + new string('e', 30);
}

/// <summary>A hand edit that is stamped and gathering, waiting for Send Changes After to end.</summary>
/// <param name="Server">The server edited on.</param>
/// <param name="Path">The file.</param>
/// <param name="Version">The stamp.</param>
/// <param name="Recorded">Whether it was made by hand.</param>
public sealed record PendingEdit(RingServer Server, string Path, ObjectVersion Version, bool Recorded);

/// <summary>A server's history of one file, as its two parts.</summary>
/// <param name="Played">Played, play count, and position.</param>
/// <param name="Favorite">Favorite.</param>
public sealed record HistoryValue(string Played, string Favorite);

/// <summary>One server in a <see cref="RingNetwork"/>.</summary>
public sealed class RingServer : IDisposable
{
    private readonly QueueTestDatabase _db = new();
    private readonly QueueLocalItems _local = new();
    private readonly Dictionary<string, Guid> _itemIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="RingServer"/> class.
    /// </summary>
    /// <param name="name">The server's name, which is also the entry key its peers know it by.</param>
    /// <param name="id">The server's id.</param>
    public RingServer(string name, string id)
    {
        Name = name;
        Id = id;
        Config = QueueSamples.Config();
        Versions = new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance);
        Outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
        Content = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
        var host = QueueSamples.Host(id);
        var worker = new OutboundHintWorker(Outbound, Config, null!, null!, null!, new HintSuppression(), null!, NullLogger<OutboundHintWorker>.Instance);
        Publisher = new LocalHintPublisher(Config, Outbound, Versions, worker, host, NullLogger<LocalHintPublisher>.Instance, links: null, content: Content);
        Triage = new HintTriage(Config, _local, Versions);
        Resolver = new VersionConflictResolver(Versions, host, Config, NullLogger<VersionConflictResolver>.Instance);
    }

    /// <summary>Gets the server's name.</summary>
    public string Name { get; }

    /// <summary>Gets the server's id.</summary>
    public string Id { get; }

    /// <summary>Gets the server's configuration.</summary>
    public FakeConfigManager Config { get; }

    /// <summary>Gets the server's stamps.</summary>
    public VersionStore Versions { get; }

    /// <summary>Gets the server's outbound queue.</summary>
    public OutboundHintStore Outbound { get; }

    /// <summary>Gets the server's content table.</summary>
    public ContentSyncTableManager Content { get; }

    /// <summary>Gets the server's publisher.</summary>
    public LocalHintPublisher Publisher { get; }

    /// <summary>Gets the server's triage.</summary>
    public HintTriage Triage { get; }

    /// <summary>Gets the server's resolver.</summary>
    public VersionConflictResolver Resolver { get; }

    /// <summary>Gets the server's apply guard.</summary>
    public ApplyGuard Guard { get; } = new();

    /// <summary>Gets the metadata value of each file.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the history of each file.</summary>
    public Dictionary<string, HistoryValue> History { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the playlists by key, where a missing key is a deleted or never made playlist.</summary>
    public Dictionary<string, string> Playlists { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the local id of each playlist.</summary>
    public Dictionary<string, Guid> PlaylistIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the media files present.</summary>
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the local id of each media file.</summary>
    public Dictionary<string, Guid> FileIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the answers this server gave to hints, as sender, kind, and code.</summary>
    public List<(string From, HintKind Kind, HintCode Code)> Answers { get; } = new();

    /// <summary>Gets or sets work to run after a metadata hint applied, such as a save event that slips past the guard.</summary>
    public Action<RingServer, string>? AfterApply { get; set; }

    /// <summary>Adds or updates this server's entry for a peer.</summary>
    /// <param name="peer">The peer.</param>
    /// <param name="sends">Whether this server sends to the peer, otherwise it receives from it.</param>
    public void SetEntry(RingServer peer, bool sends)
    {
        var entry = Config.Configuration.Servers.FirstOrDefault(s => s.Key == peer.Name);
        if (entry is null)
        {
            entry = new SourceServer
            {
                Key = peer.Name,
                Name = "Server " + peer.Name,
                Url = "http://" + peer.Name.ToLowerInvariant() + ":8096",
                ApiKey = "key",
                ServerId = peer.Id,
                Mode = sends ? ServerMode.Push : ServerMode.Pull,
                LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib", SourceRootPath = RingNetwork.Root, LocalLibraryId = "lib", LocalRootPath = RingNetwork.Root } },
                UserMappings = { new UserMapping { SourceUserId = RingNetwork.User.ToString("N"), LocalUserId = RingNetwork.User.ToString("N") } }
            };
            Config.Configuration.Servers.Add(entry);
            return;
        }

        if ((sends && entry.Mode == ServerMode.Pull) || (!sends && entry.Mode == ServerMode.Push))
        {
            entry.Mode = ServerMode.Sync;
        }
    }

    /// <summary>Adds a media file with its own local id and a starting value.</summary>
    /// <param name="path">The path.</param>
    /// <param name="value">The starting metadata value.</param>
    public void AddItem(string path, string value)
    {
        var item = _local.AddItem(path);
        _itemIds[path] = item.Id;
        Values[path] = value;
        History[path] = new HistoryValue("unplayed", "no");
    }

    /// <summary>Gets the local id of a file.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The id.</returns>
    public Guid ItemId(string path) => _itemIds[path];

    /// <summary>Gets this server's metadata key for a file.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The key.</returns>
    public string MetadataKey(string path) => HintProtocol.MetadataKey(_itemIds[path]);

    /// <summary>Gets this server's history key for a file.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The key.</returns>
    public string HistoryKey(string path) => HintProtocol.HistoryKey(RingNetwork.User, _itemIds[path]);

    /// <summary>Sets one part of a file's history.</summary>
    /// <param name="path">The path.</param>
    /// <param name="part">The part.</param>
    /// <param name="value">The value.</param>
    public void SetHistory(string path, HistoryParts part, string value)
        => History[path] = part == HistoryParts.Played ? History[path] with { Played = value } : History[path] with { Favorite = value };

    /// <summary>Gets the metadata stamp of a file.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The stamp, or null.</returns>
    public ObjectVersion? Stamp(string path) => Versions.Get(HintKind.Metadata, MetadataKey(path));

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();
}
