using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Combines two servers' versions of one user's permissions when both changed since they last agreed,
/// one permission at a time: the more generous of the two for <see cref="UserConflictRule.Permissive"/>,
/// the stricter for <see cref="UserConflictRule.Restrictive"/>. Both servers reach the same answer from
/// the same two values, whichever of them does the combining. A permission with no more and no less,
/// and any this class does not know, takes the other server's value. A disabled user stays disabled
/// under either rule, so a lockout on one server is never undone by the other.
/// </summary>
public static class UserPolicyCombiner
{
    // Switches that grant something when on.
    private static readonly HashSet<string> GrantsWhenOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnableUserPreferenceAccess", "EnableRemoteControlOfOtherUsers", "EnableSharedDeviceControl", "EnableRemoteAccess",
        "EnableLiveTvManagement", "EnableLiveTvAccess", "EnableMediaPlayback", "EnableAudioPlaybackTranscoding",
        "EnableVideoPlaybackTranscoding", "EnablePlaybackRemuxing", "EnableContentDeletion", "EnableContentDownloading",
        "EnableSyncTranscoding", "EnableMediaConversion", "EnablePublicSharing", "EnableCollectionManagement",
        "EnableSubtitleManagement", "EnableLyricManagement"
    };

    // Switches that take something away when on.
    private static readonly HashSet<string> RestrictsWhenOn = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsHidden", "ForceRemoteSourceTranscoding"
    };

    // Limits where a higher number allows more, with the value that means no limit at all.
    private static readonly Dictionary<string, long?> Limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaxActiveSessions"] = 0,
        ["RemoteClientBitrateLimit"] = 0,
        ["LoginAttemptsBeforeLockout"] = -1,
        ["MaxParentalRating"] = null,
        ["MaxParentalSubRating"] = null
    };

    // Lists of what is blocked: a longer list allows less.
    private static readonly HashSet<string> BlockLists = new(StringComparer.OrdinalIgnoreCase) { "BlockedTags", "BlockUnratedItems" };

    // Lists of the only things allowed: an empty list allows everything.
    private static readonly HashSet<string> AllowLists = new(StringComparer.OrdinalIgnoreCase) { "AllowedTags" };

    // Choices where a lower number allows more.
    private static readonly HashSet<string> LowerAllowsMore = new(StringComparer.OrdinalIgnoreCase) { "SyncPlayAccess" };

    /// <summary>Combines two policies.</summary>
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
            if (aUnlimited || bUnlimited)
            {
                // No limit is the most generous value, so it wins for Permissive and loses for Restrictive.
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
            if (theirsEmpty || oursEmpty)
            {
                // An empty list allows everything.
                return generous ? Array.Empty<JsonElement>() : (theirsEmpty ? ours : theirs);
            }

            return generous ? Either(theirs, ours) : Both(theirs, ours);
        }

        return theirs;
    }

    private static bool IsBool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsNumberOrNull(JsonElement value) => value.ValueKind is JsonValueKind.Number or JsonValueKind.Null;

    private static long? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    // What is in either list, in a stable order so both servers write the same list.
    private static List<JsonElement> Either(JsonElement a, JsonElement b)
        => a.EnumerateArray().Concat(b.EnumerateArray())
            .GroupBy(e => e.GetRawText(), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.GetRawText(), StringComparer.Ordinal)
            .ToList();

    // What is in both lists, in a stable order.
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
