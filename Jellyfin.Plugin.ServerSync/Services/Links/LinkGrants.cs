using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Who a link is being read for, a user or this server's own media probe, and the server it was
/// routed to, or null for the link's own server.
/// </summary>
public sealed record LinkGrant(Guid? UserId, bool Probe, LinkRoute? Route = null);

/// <summary>
/// Ties a link to the users the source has allowed to play it. Jellyfin's ffmpeg and its stream proxy
/// read a link with no token, so a user is recorded when the source allows them at PlaybackInfo, and
/// the link route reads it from here. Only that approval makes a grant: a later stream request can
/// keep its own user's grant alive, never make one. A grant nobody uses expires quickly. Once in use it
/// lasts while playback goes on, since a player pauses and seeks with gaps between its requests. When
/// two allowed users play one link at once, the link route names the one seen most recently. Each grant
/// also keeps the server its playback streams from, since a link can fall back to another server.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkGrants
{
    /// <summary>How long a grant waits for its first use.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromMinutes(2);

    /// <summary>How long a grant lasts after its last use.</summary>
    public static readonly TimeSpan InUse = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a probe grant waits. The probe runs from Jellyfin's refresh queue, which after a large
    /// sync may reach a link long after it was written.
    /// </summary>
    public static readonly TimeSpan ProbeWait = TimeSpan.FromHours(1);

    /// <summary>
    /// The most of a file one probe grant may read. A probe reads a file's headers and index, which is
    /// a small part of it, so nothing else on this server reads a whole file under the probe's grant.
    /// </summary>
    public const long ProbeBudget = 256L * 1024 * 1024;

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();

    // Per link, the live grants by user. The probe's grant is keyed by the empty id.
    private readonly Dictionary<string, Dictionary<Guid, Entry>> _grants = new(StringComparer.OrdinalIgnoreCase);

    public LinkGrants()
        : this(() => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LinkGrants"/> class, for tests.</summary>
    /// <param name="now">The clock.</param>
    public LinkGrants(Func<DateTime> now)
    {
        _now = now;
    }

    /// <summary>Records that a source allowed a user to play a link, and which server that was.</summary>
    public void Grant(string linkId, Guid userId, LinkRoute? route = null)
    {
        lock (_lock)
        {
            var users = UsersOf(linkId);
            var used = users.TryGetValue(userId, out var current) && Live(current) && current.Used;
            users[userId] = new Entry(false, _now(), used, route, 0);
        }
    }

    /// <summary>
    /// The server a user's playback of a link streams from, or null for the link's own server. The
    /// probe's grant is named by the empty id.
    /// </summary>
    public LinkRoute? RouteFor(string linkId, Guid userId)
    {
        lock (_lock)
        {
            return _grants.TryGetValue(linkId, out var users) && users.TryGetValue(userId, out var current) && Live(current)
                ? current.Route
                : null;
        }
    }

    /// <summary>
    /// Moves a live grant to another server, after its own failed partway and another stood in. A
    /// grant that has lapsed is not brought back.
    /// </summary>
    public void Reroute(string linkId, Guid userId, LinkRoute route)
    {
        lock (_lock)
        {
            if (_grants.TryGetValue(linkId, out var users) && users.TryGetValue(userId, out var current) && Live(current))
            {
                users[userId] = current with { Route = route };
            }
        }
    }

    /// <summary>
    /// Keeps a user's grant alive while they stream. False when the user holds no live grant, so the
    /// request was never allowed by the source and must not reach the link.
    /// </summary>
    public bool Refresh(string linkId, Guid userId)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(linkId, out var users) || !users.TryGetValue(userId, out var current) || !Live(current))
            {
                return false;
            }

            users[userId] = current with { At = _now(), Used = true };
            return true;
        }
    }

    /// <summary>Lets this server's own media probe read a link nobody is playing.</summary>
    public void GrantProbe(string linkId)
    {
        lock (_lock)
        {
            var users = UsersOf(linkId);
            if (!users.Any(p => p.Key != Guid.Empty && Live(p.Value)))
            {
                users[Guid.Empty] = new Entry(true, _now(), false, null, 0);
            }
        }
    }

    /// <summary>The live grant seen most recently for a link, marked as used, or null when nobody may read it now.</summary>
    public LinkGrant? Use(string linkId)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(linkId, out var users))
            {
                return null;
            }

            foreach (var dead in users.Where(p => !Live(p.Value)).Select(p => p.Key).ToList())
            {
                users.Remove(dead);
            }

            if (users.Count == 0)
            {
                _grants.Remove(linkId);
                return null;
            }

            var (userId, entry) = users.OrderByDescending(p => p.Value.Probe ? DateTime.MinValue : p.Value.At).First();

            // A probe grant keeps its own short window from when it was made, however often it is read.
            if (!entry.Probe)
            {
                users[userId] = entry with { At = _now(), Used = true };
            }

            return new LinkGrant(entry.Probe ? null : userId, entry.Probe, entry.Route);
        }
    }

    /// <summary>
    /// Counts bytes read under a link's probe grant. False once the grant has read as much as a probe
    /// may, or is gone, which ends the read.
    /// </summary>
    public bool SpendProbe(string linkId, long bytes)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(linkId, out var users) || !users.TryGetValue(Guid.Empty, out var current) || !Live(current))
            {
                return false;
            }

            users[Guid.Empty] = current with { Spent = current.Spent + bytes };
            return true;
        }
    }

    private Dictionary<Guid, Entry> UsersOf(string linkId)
    {
        if (!_grants.TryGetValue(linkId, out var users))
        {
            users = new Dictionary<Guid, Entry>();
            _grants[linkId] = users;
        }

        return users;
    }

    private bool Live(Entry entry)
        => _now() - entry.At < (entry.Probe ? ProbeWait : entry.Used ? InUse : Unused)
            && (!entry.Probe || entry.Spent < ProbeBudget);

    private sealed record Entry(bool Probe, DateTime At, bool Used, LinkRoute? Route, long Spent);
}
