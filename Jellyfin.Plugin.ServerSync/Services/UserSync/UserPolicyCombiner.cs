using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// Combines two servers' versions of a user's permissions one at a time, taking the more generous or the stricter by the conflict rule.
/// A disabled user stays disabled under either rule, and a permission with no more and no less takes the other server's value.
/// </summary>
public static class UserPolicyCombiner
{
    /// <summary>
    /// The switches that grant something when on.
    /// </summary>
    private static readonly HashSet<string> GrantsWhenOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnableUserPreferenceAccess", "EnableRemoteControlOfOtherUsers", "EnableSharedDeviceControl", "EnableRemoteAccess",
        "EnableLiveTvManagement", "EnableLiveTvAccess", "EnableMediaPlayback", "EnableAudioPlaybackTranscoding",
        "EnableVideoPlaybackTranscoding", "EnablePlaybackRemuxing", "EnableContentDeletion", "EnableContentDownloading",
        "EnableSyncTranscoding", "EnableMediaConversion", "EnablePublicSharing", "EnableCollectionManagement",
        "EnableSubtitleManagement", "EnableLyricManagement"
    };

    /// <summary>
    /// The switches that take something away when on.
    /// </summary>
    private static readonly HashSet<string> RestrictsWhenOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsHidden", "ForceRemoteSourceTranscoding"
    };

    /// <summary>
    /// The limits where a higher number allows more, each with the value that means no limit.
    /// </summary>
    private static readonly Dictionary<string, long?> Limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaxActiveSessions"] = 0,
        ["RemoteClientBitrateLimit"] = 0,
        ["LoginAttemptsBeforeLockout"] = -1,
        ["MaxParentalRating"] = null,
        ["MaxParentalSubRating"] = null
    };

    /// <summary>
    /// The lists of what is blocked, where a longer list allows less.
    /// </summary>
    private static readonly HashSet<string> BlockLists = new(StringComparer.OrdinalIgnoreCase) { "BlockedTags", "BlockUnratedItems" };

    /// <summary>
    /// The lists of the only things allowed, where an empty list allows everything.
    /// </summary>
    private static readonly HashSet<string> AllowLists = new(StringComparer.OrdinalIgnoreCase) { "AllowedTags" };

    /// <summary>
    /// The choices where a lower number allows more.
    /// </summary>
    private static readonly HashSet<string> LowerAllowsMore = new(StringComparer.OrdinalIgnoreCase) { "SyncPlayAccess" };

    /// <summary>
    /// Combines two policies so both servers reach the same answer from the same two values.
    /// </summary>
    /// <param name="other">The other server's policy, in this server's terms, as JSON.</param>
    /// <param name="mine">This server's policy as JSON.</param>
    /// <param name="rule">Which rule settles a permission both servers changed.</param>
    /// <returns>The combined policy as JSON, or the other server's when either cannot be read.</returns>
    public static string? Combine(string? other, string? mine, UserConflictRule rule)
    {
        if (rule == UserConflictRule.Newest || string.IsNullOrEmpty(other) || string.IsNullOrEmpty(mine))
        {
            return other;
        }

        Dictionary<string, JsonElement>? theirs;
        Dictionary<string, JsonElement>? ours;
        try
        {
            theirs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(other);
            ours = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(mine);
        }
        catch (JsonException)
        {
            return other;
        }

        if (theirs is null || ours is null)
        {
            return other;
        }

        var generous = rule == UserConflictRule.Permissive;
        var combined = new Dictionary<string, object?>(theirs.Count);
        foreach (var (name, theirValue) in theirs)
        {
            combined[name] = ours.TryGetValue(name, out var ourValue) ? CombineOne(name, theirValue, ourValue, generous) : theirValue;
        }

        return JsonSerializer.Serialize(combined);
    }

    /// <summary>
    /// Combines one permission by its kind, or takes the other server's value for a kind that is not known.
    /// </summary>
    private static object? CombineOne(string name, JsonElement theirs, JsonElement ours, bool generous)
    {
        if (string.Equals(name, "IsDisabled", StringComparison.OrdinalIgnoreCase) && IsBool(theirs) && IsBool(ours))
        {
            return theirs.GetBoolean() || ours.GetBoolean();
        }

        if (GrantsWhenOn.Contains(name) && IsBool(theirs) && IsBool(ours))
        {
            return generous ? theirs.GetBoolean() || ours.GetBoolean() : theirs.GetBoolean() && ours.GetBoolean();
        }

        if (RestrictsWhenOn.Contains(name) && IsBool(theirs) && IsBool(ours))
        {
            return generous ? theirs.GetBoolean() && ours.GetBoolean() : theirs.GetBoolean() || ours.GetBoolean();
        }

        if (Limits.TryGetValue(name, out var unlimited) && IsNumberOrNull(theirs) && IsNumberOrNull(ours))
        {
            var a = Number(theirs);
            var b = Number(ours);
            var aUnlimited = a == unlimited;
            var bUnlimited = b == unlimited;
            // No limit is the most generous value.
            if (aUnlimited || bUnlimited)
            {
                return generous ? unlimited : (aUnlimited ? b : a);
            }

            return generous ? Math.Max(a!.Value, b!.Value) : Math.Min(a!.Value, b!.Value);
        }

        if (LowerAllowsMore.Contains(name) && theirs.ValueKind == JsonValueKind.Number && ours.ValueKind == JsonValueKind.Number)
        {
            return generous ? Math.Min(theirs.GetInt64(), ours.GetInt64()) : Math.Max(theirs.GetInt64(), ours.GetInt64());
        }

        if (BlockLists.Contains(name) && theirs.ValueKind == JsonValueKind.Array && ours.ValueKind == JsonValueKind.Array)
        {
            return generous ? Both(theirs, ours) : Either(theirs, ours);
        }

        if (AllowLists.Contains(name) && theirs.ValueKind == JsonValueKind.Array && ours.ValueKind == JsonValueKind.Array)
        {
            var theirsEmpty = theirs.GetArrayLength() == 0;
            var oursEmpty = ours.GetArrayLength() == 0;
            // An empty list allows everything.
            if (theirsEmpty || oursEmpty)
            {
                return generous ? Array.Empty<JsonElement>() : (theirsEmpty ? ours : theirs);
            }

            return generous ? Either(theirs, ours) : Both(theirs, ours);
        }

        return theirs;
    }

    private static bool IsBool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsNumberOrNull(JsonElement value) => value.ValueKind is JsonValueKind.Number or JsonValueKind.Null;

    private static long? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>
    /// Gets what is in either list, in a stable order so both servers write the same list.
    /// </summary>
    private static List<JsonElement> Either(JsonElement a, JsonElement b)
        => a.EnumerateArray().Concat(b.EnumerateArray())
            .GroupBy(e => e.GetRawText(), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.GetRawText(), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Gets what is in both lists, in a stable order.
    /// </summary>
    private static List<JsonElement> Both(JsonElement a, JsonElement b)
    {
        var inB = new HashSet<string>(b.EnumerateArray().Select(e => e.GetRawText()), StringComparer.Ordinal);
        return a.EnumerateArray()
            .Where(e => inB.Contains(e.GetRawText()))
            .GroupBy(e => e.GetRawText(), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.GetRawText(), StringComparer.Ordinal)
            .ToList();
    }
}
