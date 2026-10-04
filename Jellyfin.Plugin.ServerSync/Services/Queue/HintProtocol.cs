using System;
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
    /// The most pending rows kept for one peer. A paused peer accumulates rows without bound otherwise;
    /// beyond the cap the oldest pending rows are dropped, since the scheduled tasks recover anything a
    /// dropped hint would have carried.
    /// </summary>
    public const int MaxPendingPerPeer = 10000;

    /// <summary>How long local edits to one object are gathered before one hint is raised.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

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

    /// <summary>How often local users are checked for policy and configuration changes, which Jellyfin raises no event for.</summary>
    public static readonly TimeSpan UserPoll = TimeSpan.FromSeconds(30);

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
    /// hour for every attempt after that. Nothing is ever given up.
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
