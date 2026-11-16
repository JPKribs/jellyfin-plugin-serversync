using System;
using System.Collections.Generic;
using System.Globalization;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>Holds the constants and key shapes shared by both ends of the hint exchange.</summary>
public static class HintProtocol
{
    /// <summary>The most hints one queue request may carry.</summary>
    public const int MaxHintsPerRequest = 500;

    /// <summary>
    /// How long a sent hint may wait before the sender asks the peer whether it still holds it.
    /// It is short so a peer that cannot report completion is cleared up within minutes.
    /// </summary>
    public static readonly TimeSpan CompletionGrace = TimeSpan.FromMinutes(10);

    /// <summary>Gives how long a peer that refused the API Key or lacks the plugin stays paused before another try.</summary>
    /// <param name="config">The configuration, which holds the minutes.</param>
    /// <returns>The pause.</returns>
    public static TimeSpan PeerPause(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TimeSpan.FromMinutes(Math.Clamp(config.HintRefusedRetryMinutes, 1, 1440));
    }

    /// <summary>
    /// Gives how long to wait before sending a hint again after a peer could not be reached.
    /// It is the configured first wait, doubled for every failure after the first, up to the configured ceiling.
    /// </summary>
    /// <param name="config">The configuration, which holds the first wait and the ceiling.</param>
    /// <param name="attemptsSoFar">How many attempts have failed, including this one.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan OutboundDelay(PluginConfiguration config, int attemptsSoFar)
    {
        ArgumentNullException.ThrowIfNull(config);
        var first = TimeSpan.FromSeconds(Math.Clamp(config.HintRetrySeconds, 5, 3600));
        var ceiling = TimeSpan.FromMinutes(Math.Clamp(config.HintRetryMaxMinutes, 1, 1440));

        // Past thirty doublings the wait exceeds any ceiling, and the shift would overflow.
        var doublings = Math.Clamp(attemptsSoFar - 1, 0, 30);
        var delay = first * (1L << doublings);
        return delay < ceiling ? delay : ceiling;
    }

    /// <summary>Whether a hint has used up its attempts.</summary>
    /// <param name="maxRetries">The configured most attempts, where zero means no limit.</param>
    /// <param name="attemptsSoFar">How many attempts have failed, including this one.</param>
    /// <returns><c>true</c> when the hint should be marked failed instead of tried again.</returns>
    public static bool OutOfRetries(int maxRetries, int attemptsSoFar) => maxRetries > 0 && attemptsSoFar >= maxRetries;

    /// <summary>
    /// How far a peer's clock may differ from this server's before Check Link says so.
    /// Two edits of one object made within the skew are decided by the wrong clock.
    /// </summary>
    public static readonly TimeSpan ClockSkewWarning = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most pending rows kept for one peer, so a paused peer cannot gather rows without bound.
    /// Beyond it the oldest are dropped, since the scheduled tasks recover anything a dropped hint carried.
    /// </summary>
    public const int MaxPendingPerPeer = 10000;

    /// <summary>
    /// Gives how long local edits to one object are gathering after the last one before one hint is raised.
    /// Every further edit restarts the wait, so a stream of edits becomes one hint once it stops.
    /// </summary>
    /// <param name="config">The configuration, which holds the seconds.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Debounce(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TimeSpan.FromSeconds(Math.Clamp(config.HintDebounceSeconds, 1, 3600));
    }

    /// <summary>How often a sender reads again what each peer accepts, so a sync type turned on there is noticed within a minute.</summary>
    public static readonly TimeSpan CapabilityRefresh = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far ahead of this server's clock a peer's stamp may be before the hint is declined.
    /// A smaller lead is honest skew and is read as the current time, so it never locks an object out of later edits.
    /// </summary>
    public static readonly TimeSpan MaxVersionLead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many failed attempts a received hint gets before it is dropped, which is about a day of trying.
    /// The scheduled tasks carry the change after that.
    /// </summary>
    public const int MaxInboundAttempts = 24;

    /// <summary>The most completed hint ids a receiver remembers for peers that read them from its status.</summary>
    public const int MaxRememberedCompletions = 10000;

    /// <summary>
    /// How long one call between peers may take, from sending to the last byte of the answer.
    /// The HTTP client's own timeout ends at the headers, so a peer that stops mid answer would otherwise hold a worker forever.
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
    /// How long a hint for an item this server does not have yet waits for it.
    /// A new item's metadata comes with it through the metadata provider, so a hint still waiting after this is dropped.
    /// </summary>
    public static readonly TimeSpan ParkWindow = TimeSpan.FromHours(6);

    /// <summary>The header carrying the caller's plugin version.</summary>
    public const string VersionHeader = "X-ServerSync-Version";

    /// <summary>The header carrying the protocol the caller speaks.</summary>
    public const string ProtocolHeader = "X-ServerSync-Protocol";

    /// <summary>The header carrying the oldest protocol the caller still speaks.</summary>
    public const string MinProtocolHeader = "X-ServerSync-Min-Protocol";

    /// <summary>
    /// The protocol this build speaks, kept apart from the plugin version so a patch release does not cut a pool off.
    /// Raise it only when a hint, a peer route, or a decision rule changes in a way an older peer would misread.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The oldest protocol this build still speaks, raised when support for an older one is dropped.
    /// A build that keeps this below <see cref="ProtocolVersion"/> must still answer the older protocol's requests as it expects.
    /// </summary>
    public const int MinProtocolVersion = 1;

    /// <summary>The status a receiver answers when the caller's protocol is not compatible with its own.</summary>
    public const int VersionMismatchStatus = 426;

    /// <summary>Whether two plugin versions are the same, ignoring surrounding space, where a missing version matches nothing.</summary>
    /// <param name="a">One plugin version to compare.</param>
    /// <param name="b">The plugin version to compare it with.</param>
    /// <returns><c>true</c> when both name the same version.</returns>
    public static bool SameVersion(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Whether a peer speaks a protocol this build can talk to, which holds when each side's protocol is at least the other's minimum.
    /// A peer that sends no protocol is compatible only when it runs exactly this plugin version.
    /// </summary>
    /// <param name="peerProtocol">The peer's protocol, or null when it sent none.</param>
    /// <param name="peerMinProtocol">The oldest protocol the peer speaks, or null for its own protocol.</param>
    /// <param name="peerVersion">The peer's plugin version.</param>
    /// <param name="thisVersion">This server's plugin version.</param>
    /// <returns><c>true</c> when the two can exchange hints.</returns>
    public static bool Compatible(int? peerProtocol, int? peerMinProtocol, string? peerVersion, string thisVersion)
    {
        if (peerProtocol is not { } protocol)
        {
            return SameVersion(peerVersion, thisVersion);
        }

        var peerMin = peerMinProtocol ?? protocol;
        return protocol >= MinProtocolVersion && ProtocolVersion >= peerMin;
    }

    /// <summary>Whether a peer's advertised capabilities are compatible with this build.</summary>
    /// <param name="capabilities">The peer's capabilities.</param>
    /// <param name="thisVersion">This server's plugin version.</param>
    /// <returns><c>true</c> when the two can exchange hints.</returns>
    public static bool Compatible(PeerCapabilities capabilities, string thisVersion)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return Compatible(capabilities.ProtocolVersion, capabilities.MinProtocolVersion, capabilities.PluginVersion, thisVersion);
    }

    /// <summary>Describes a plugin version mismatch with a peer that sends no protocol.</summary>
    /// <param name="peerVersion">The peer's plugin version.</param>
    /// <param name="thisVersion">This server's plugin version.</param>
    /// <returns>The reason.</returns>
    public static string VersionMismatch(string? peerVersion, string thisVersion)
        => $"Server Sync {(string.IsNullOrWhiteSpace(peerVersion) ? "of an unknown version" : peerVersion.Trim())} on the peer and {thisVersion} on this server differ. Update both servers to the same version";

    /// <summary>Describes why a peer's protocol is not compatible with this build.</summary>
    /// <param name="peerProtocol">The peer's protocol, or null when it sent none.</param>
    /// <param name="peerMinProtocol">The oldest protocol the peer speaks.</param>
    /// <param name="peerVersion">The peer's plugin version.</param>
    /// <param name="thisVersion">This server's plugin version.</param>
    /// <returns>The reason, for the dashboard and the log.</returns>
    public static string ProtocolMismatch(int? peerProtocol, int? peerMinProtocol, string? peerVersion, string thisVersion)
    {
        if (peerProtocol is not { } protocol)
        {
            return VersionMismatch(peerVersion, thisVersion);
        }

        var peer = string.IsNullOrWhiteSpace(peerVersion) ? "Server Sync on the peer" : $"Server Sync {peerVersion.Trim()} on the peer";
        var older = protocol < MinProtocolVersion ? "the peer" : "this server";
        return $"{peer} speaks protocol {protocol} (oldest {peerMinProtocol ?? protocol}) and Server Sync {thisVersion} on this server speaks {ProtocolVersion} (oldest {MinProtocolVersion}). Update {older} so the two overlap";
    }

    /// <summary>Describes why a peer's capabilities are not compatible with this build.</summary>
    /// <param name="capabilities">The peer's capabilities.</param>
    /// <param name="thisVersion">This server's plugin version.</param>
    /// <returns>The reason.</returns>
    public static string ProtocolMismatch(PeerCapabilities capabilities, string thisVersion)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return ProtocolMismatch(capabilities.ProtocolVersion, capabilities.MinProtocolVersion, capabilities.PluginVersion, thisVersion);
    }

    /// <summary>
    /// Bounds a stamp from elsewhere to this server's clock, since a time in the future would beat every later edit here.
    /// </summary>
    /// <param name="timestamp">The time the stamp carries.</param>
    /// <param name="utcNow">This server's time, which is the latest the result may be.</param>
    /// <returns>The time, or this server's time when it lay ahead.</returns>
    public static DateTime BoundVersion(DateTime timestamp, DateTime utcNow)
    {
        var at = UtcTime.AsUtc(timestamp);
        return at > utcNow ? utcNow : at;
    }

    /// <summary>
    /// Builds the stamp a received hint carries, bounded to this server's clock, for deciding and recording.
    /// The row keeps the sender's own time, since the sender completes a row only on the time it sent.
    /// </summary>
    /// <param name="hint">The received hint whose stamp is read.</param>
    /// <param name="kind">The sync type to key the stamp under, which is the hint's own unless a caller keys it otherwise.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <returns>The stamp.</returns>
    public static ObjectVersion IncomingVersion(InboundHint hint, HintKind kind, string localKey)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return new ObjectVersion { Kind = kind, Key = localKey, ServerId = hint.VersionServerId, Timestamp = BoundVersion(hint.VersionTimestamp, DateTime.UtcNow) };
    }

    /// <summary>Whether a stamp lies too far ahead of this server's clock to be trusted at all.</summary>
    /// <param name="timestamp">The time the stamp carries.</param>
    /// <param name="utcNow">This server's time, which the lead is measured from.</param>
    /// <returns><c>true</c> when it is beyond the allowed lead.</returns>
    public static bool IsFutureVersion(DateTime timestamp, DateTime utcNow) => UtcTime.AsUtc(timestamp) - utcNow > MaxVersionLead;

    /// <summary>
    /// Lists the kinds a server applies from hints, by which sync types are on.
    /// Users are never among them, since Jellyfin raises no event for user settings and the scheduled task carries them.
    /// </summary>
    /// <param name="config">The configuration, which says which sync types are on.</param>
    /// <returns>The kinds whose sync type is on.</returns>
    public static IReadOnlyList<HintKind> AcceptedKinds(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var kinds = new List<HintKind>(6);
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

        if (config.EnablePlaylistSync)
        {
            kinds.Add(HintKind.Playlists);
        }

        return kinds;
    }

    /// <summary>Builds the history key for a user and an item.</summary>
    /// <param name="userId">The user the history belongs to.</param>
    /// <param name="itemId">The item the history is for.</param>
    /// <returns>The key.</returns>
    public static string HistoryKey(Guid userId, Guid itemId)
        => userId.ToString("N", CultureInfo.InvariantCulture) + "|" + itemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Splits a history key back into its ids.</summary>
    /// <param name="key">The history key, with or without a part name after it.</param>
    /// <param name="userId">The user id read from the key.</param>
    /// <param name="itemId">The item id read from the key.</param>
    /// <returns><c>true</c> when the key was well formed.</returns>
    public static bool TryParseHistoryKey(string? key, out Guid userId, out Guid itemId)
    {
        userId = Guid.Empty;
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // A part's stamp is keyed by the history key followed by the part's name.
        var part = key.IndexOf('#', StringComparison.Ordinal);
        if (part > 0)
        {
            key = key[..part];
        }

        var split = key.IndexOf('|', StringComparison.Ordinal);
        if (split <= 0 || split == key.Length - 1)
        {
            return false;
        }

        return Guid.TryParse(key.AsSpan(0, split), out userId) && Guid.TryParse(key.AsSpan(split + 1), out itemId);
    }

    /// <summary>Builds the metadata key for an item.</summary>
    /// <param name="itemId">The item the key names.</param>
    /// <returns>The key.</returns>
    public static string MetadataKey(Guid itemId) => itemId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Builds the playlist key, which the original playlist and its copies share.</summary>
    /// <param name="rootId">The id of the original playlist.</param>
    /// <returns>The key.</returns>
    public static string PlaylistKey(Guid rootId) => rootId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The item type a playlist hint carries for an original playlist.</summary>
    public const string PlaylistOriginal = "Playlist";

    /// <summary>The item type a playlist hint carries for a copy of a playlist.</summary>
    public const string PlaylistCopy = "PlaylistCopy";

    private const string PlayedPart = "+Played";
    private const string FavoritePart = "+Favorite";

    /// <summary>
    /// Builds the item type a history hint carries, which is the item's Jellyfin type and then the parts that changed.
    /// A receiver takes those parts and leaves the others, so a play on one server does not undo a favorite the sender never heard of.
    /// </summary>
    /// <param name="itemType">The item's Jellyfin type, such as Movie.</param>
    /// <param name="parts">The parts that were changed, or none when that is not known.</param>
    /// <returns>The item type to send.</returns>
    public static string? HistoryItemType(string? itemType, HistoryParts parts)
        => parts == HistoryParts.None
            ? itemType
            : (itemType ?? string.Empty) + (parts.HasFlag(HistoryParts.Played) ? PlayedPart : string.Empty) + (parts.HasFlag(HistoryParts.Favorite) ? FavoritePart : string.Empty);

    /// <summary>Splits a history hint's item type into the item's Jellyfin type and the parts that changed.</summary>
    /// <param name="itemType">The item type the hint carries.</param>
    /// <param name="parts">The parts, or none when the sender did not say.</param>
    /// <returns>The item's Jellyfin type.</returns>
    public static string? SplitHistoryItemType(string? itemType, out HistoryParts parts)
    {
        parts = HistoryParts.None;
        if (string.IsNullOrEmpty(itemType))
        {
            return itemType;
        }

        if (itemType.Contains(PlayedPart, StringComparison.Ordinal))
        {
            parts |= HistoryParts.Played;
        }

        if (itemType.Contains(FavoritePart, StringComparison.Ordinal))
        {
            parts |= HistoryParts.Favorite;
        }

        var cut = itemType.IndexOf('+', StringComparison.Ordinal);
        var type = cut < 0 ? itemType : itemType[..cut];
        return type.Length == 0 ? null : type;
    }

    /// <summary>Builds the key one part of an object's history is stamped under on this server.</summary>
    /// <param name="historyKey">The history key of the user and item.</param>
    /// <param name="part">The part to key, which is played or favorite.</param>
    /// <returns>The key.</returns>
    public static string HistoryPartKey(string historyKey, HistoryParts part)
        => historyKey + (part == HistoryParts.Favorite ? "#favorite" : "#played");

    /// <summary>The item type a playlist hint carries when an original playlist was deleted.</summary>
    public const string PlaylistDeleted = "PlaylistDeleted";

    /// <summary>The item type a playlist hint carries when a copy of a playlist was deleted.</summary>
    public const string PlaylistCopyDeleted = "PlaylistCopyDeleted";

    /// <summary>Picks the item type a playlist hint carries.</summary>
    /// <param name="copy">Whether the playlist is a copy.</param>
    /// <param name="deleted">Whether the playlist was deleted.</param>
    /// <returns>The item type.</returns>
    public static string PlaylistItemType(bool copy, bool deleted)
        => deleted ? (copy ? PlaylistCopyDeleted : PlaylistDeleted) : (copy ? PlaylistCopy : PlaylistOriginal);

    /// <summary>
    /// The item type a content hint carries when the file left the library, in place of the item's Jellyfin type.
    /// </summary>
    public const string ContentRemoved = "Removed";

    /// <summary>
    /// How long a file that left the library waits before its removal is announced.
    /// A rename or a new container announces the new file first, so a receiver can move or replace its copy instead of deleting it.
    /// </summary>
    public static readonly TimeSpan RemovalHold = TimeSpan.FromMinutes(10);

    /// <summary>How recent a file's time must be for a rewrite to be announced when no stamp says when the file was last announced.</summary>
    public static readonly TimeSpan UnstampedRewriteWindow = TimeSpan.FromDays(1);

    /// <summary>Whether a content hint says its file left the library.</summary>
    /// <param name="itemType">The item type the hint carries.</param>
    /// <returns><c>true</c> for a removal.</returns>
    public static bool IsContentRemoval(string? itemType)
        => string.Equals(itemType, ContentRemoved, StringComparison.Ordinal);

    /// <summary>Whether a playlist hint's item type is a deletion.</summary>
    /// <param name="itemType">The item type the hint carries.</param>
    /// <returns><c>true</c> for a deletion.</returns>
    public static bool IsPlaylistDeletion(string? itemType)
        => itemType is PlaylistDeleted or PlaylistCopyDeleted;

    /// <summary>Whether a playlist hint's item type is a copy.</summary>
    /// <param name="itemType">The item type the hint carries.</param>
    /// <returns><c>true</c> for a copy.</returns>
    public static bool IsPlaylistCopy(string? itemType)
        => itemType is PlaylistCopy or PlaylistCopyDeleted;

    /// <summary>Builds the users key for a user.</summary>
    /// <param name="userId">The user the key names.</param>
    /// <returns>The key.</returns>
    public static string UsersKey(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Builds the people key for a person, which is the name folded for comparison.</summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The key.</returns>
    public static string PeopleKey(string name) => (name ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Builds the guard key that covers every object of a kind, used while an apply has side effects on other objects.</summary>
    /// <param name="kind">The sync type to cover.</param>
    /// <returns>The guard key.</returns>
    public static string GuardAllKey(HintKind kind) => GuardKey(kind, "*");

    /// <summary>Builds the name under which an apply of one object is registered with the guard.</summary>
    /// <param name="kind">The sync type of the object.</param>
    /// <param name="localKey">This server's key for the object.</param>
    /// <returns>The guard key.</returns>
    public static string GuardKey(HintKind kind, string localKey) => ((int)kind).ToString(CultureInfo.InvariantCulture) + ":" + localKey;

    /// <summary>
    /// Gives how long a received hint waits after a failure, which is one minute, five, fifteen, then an hour each time.
    /// A sent hint waits by <see cref="OutboundDelay"/> instead.
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
