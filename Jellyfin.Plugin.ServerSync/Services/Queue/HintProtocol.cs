using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Constants and key shapes shared by both ends of the hint exchange.
/// </summary>
public static class HintProtocol
{
    /// <summary>The capability a peer advertises when it accepts hints.</summary>
    public const string HintFeature = "hints";

    /// <summary>The most hints one queue request may carry.</summary>
    public const int MaxHintsPerRequest = 500;

    /// <summary>
    /// How long a sent hint may wait before the sender asks the peer whether it still holds it. Short
    /// enough that a peer which cannot report completion, because it holds a standard user's key for
    /// this server, is cleared up within minutes rather than hours.
    /// </summary>
    public static readonly TimeSpan CompletionGrace = TimeSpan.FromMinutes(10);

    /// <summary>How long a peer that refused the key or lacks the plugin is left alone before another try.</summary>
    public static readonly TimeSpan PeerPause = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far a peer's clock may differ from this server's before Check Link says so. Two edits of one
    /// object made within the skew are decided by the wrong clock.
    /// </summary>
    public static readonly TimeSpan ClockSkewWarning = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most pending rows kept for one peer. A paused peer accumulates rows without bound otherwise.
    /// beyond the cap the oldest pending rows are dropped, since the scheduled tasks recover anything a
    /// dropped hint would have carried.
    /// </summary>
    public const int MaxPendingPerPeer = 10000;

    /// <summary>
    /// How long local edits to one object are gathered after the last one before one hint is raised.
    /// Every further edit restarts the wait, so a stream of edits becomes one hint once it stops.
    /// </summary>
    /// <param name="config">The configuration, which holds the seconds.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Debounce(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TimeSpan.FromSeconds(Math.Clamp(config.HintDebounceSeconds, 1, 3600));
    }

    /// <summary>How often a sender re-reads what each peer accepts, so a module switched on there is noticed within a minute.</summary>
    public static readonly TimeSpan CapabilityRefresh = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far ahead of this server's clock a peer's version may be before the hint is declined. A
    /// version that is older than that is still honest skew and is clamped to now, so it can never
    /// lock an object out of every later real edit.
    /// </summary>
    public static readonly TimeSpan MaxVersionLead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many failed attempts a received hint gets before it is dropped. With backoff that holds at an
    /// hour, this is about a day of trying. The scheduled tasks carry the change after that.
    /// </summary>
    public const int MaxInboundAttempts = 24;

    /// <summary>The most completed hint ids a receiver remembers for peers that read them from its status.</summary>
    public const int MaxRememberedCompletions = 10000;

    /// <summary>
    /// How long one call between peers may take, from sending to the last byte of the answer. The
    /// answer is read after its headers arrive, so the HTTP client's own timeout does not cover it. A
    /// peer that stops mid answer would otherwise hold a worker forever.
    /// </summary>
    public static readonly TimeSpan PeerCallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long opening a connection to a peer may take before the attempt is abandoned.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The most bytes a peer's answer to a hint request may carry.</summary>
    public const long MaxPeerResponseBytes = 32L * 1024 * 1024;

    /// <summary>The header a server presents on requests that name it as the sender, holding the secret the receiver issued to it.</summary>
    public const string PairingHeader = "X-ServerSync-Pairing";

    /// <summary>The status a receiver answers when the sender is not paired with it, so the sender pairs and tries again.</summary>
    public const int UnpairedStatus = 428;

    /// <summary>
    /// Bounds a version from elsewhere to this server's clock. A timestamp in the future would beat every
    /// later real edit here, so it is read as now.
    /// </summary>
    /// <param name="timestamp">The version's timestamp.</param>
    /// <param name="utcNow">This server's clock.</param>
    /// <returns>The timestamp, or now when it lay ahead.</returns>
    public static DateTime BoundVersion(DateTime timestamp, DateTime utcNow)
    {
        var at = AsUtc(timestamp);
        return at > utcNow ? utcNow : at;
    }

    private static DateTime AsUtc(DateTime timestamp) => Utilities.UtcTime.AsUtc(timestamp);

    /// <summary>
    /// The version a received hint carries, bounded to this server's clock, for deciding and recording.
    /// The row itself keeps the sender's own timestamp, since the sender only completes a row on the
    /// timestamp it sent. A bounded one would never match and the hint would be sent forever.
    /// </summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="kind">The kind, which is the hint's own unless a caller keys it otherwise.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <returns>The version.</returns>
    public static ObjectVersion IncomingVersion(InboundHint hint, HintKind kind, string localKey)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return new ObjectVersion { Kind = kind, Key = localKey, ServerId = hint.VersionServerId, Timestamp = BoundVersion(hint.VersionTimestamp, DateTime.UtcNow) };
    }

    /// <summary>Whether a version lies too far ahead of this server's clock to be trusted at all.</summary>
    /// <param name="timestamp">The version's timestamp.</param>
    /// <param name="utcNow">This server's clock.</param>
    /// <returns>True when it is beyond the allowed lead.</returns>
    public static bool IsFutureVersion(DateTime timestamp, DateTime utcNow)
    {
        return AsUtc(timestamp) - utcNow > MaxVersionLead;
    }

    /// <summary>The kinds a server applies from hints, by its module switches. Users are never among them.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The kinds whose module is on.</returns>
    public static IReadOnlyList<HintKind> AcceptedKinds(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var kinds = new List<HintKind>(5);
        if (config.EnableHistorySync)
        {
            kinds.Add(HintKind.History);
        }

        if (config.EnableMetadataSync)
        {
            kinds.Add(HintKind.Metadata);
        }

        if (config.EnablePeopleSync)
        {
            kinds.Add(HintKind.People);
        }

        if (config.EnableContentSync)
        {
            kinds.Add(HintKind.Content);
        }

        // User settings are never announced live: Jellyfin raises no event for policy and configuration
        // changes, so the scheduled task carries them.
        return kinds;
    }

    /// <summary>Builds the history key for a user and an item.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The key.</returns>
    public static string HistoryKey(Guid userId, Guid itemId)
        => userId.ToString("N", CultureInfo.InvariantCulture) + "|" + itemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Splits a history key back into its ids.</summary>
    /// <param name="key">The key.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> when the key was well formed.</returns>
    public static bool TryParseHistoryKey(string? key, out Guid userId, out Guid itemId)
    {
        userId = Guid.Empty;
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var split = key.IndexOf('|', StringComparison.Ordinal);
        if (split <= 0 || split == key.Length - 1)
        {
            return false;
        }

        return Guid.TryParse(key.AsSpan(0, split), out userId) && Guid.TryParse(key.AsSpan(split + 1), out itemId);
    }

    /// <summary>Builds the metadata key for an item.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The key.</returns>
    public static string MetadataKey(Guid itemId) => itemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Builds the users key for a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The key.</returns>
    public static string UsersKey(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Builds the people key for a person, which is the name folded for comparison.</summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The key.</returns>
    public static string PeopleKey(string name) => (name ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>The guard key that covers every object of a kind, used while an apply has side effects on other objects.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The guard key.</returns>
    public static string GuardAllKey(HintKind kind) => GuardKey(kind, "*");

    /// <summary>The name under which an apply of one object is registered with the guard.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="localKey">This server's key.</param>
    /// <returns>The guard key.</returns>
    public static string GuardKey(HintKind kind, string localKey) => ((int)kind).ToString(CultureInfo.InvariantCulture) + ":" + localKey;

    /// <summary>
    /// How long to wait before the next attempt after a failure: one minute, five, fifteen, then one
    /// hour for every attempt after that. A received hint gives up after <see cref="MaxInboundAttempts"/>
    /// attempts, and a sent one keeps trying until the peer answers or the row expires.
    /// </summary>
    /// <param name="attemptsSoFar">How many attempts have failed, including this one.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan NextDelay(int attemptsSoFar) => attemptsSoFar switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };
}
