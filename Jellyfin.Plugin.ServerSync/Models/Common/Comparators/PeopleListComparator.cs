// CA5351 — SHA256 here is a content fingerprint, not a security primitive.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Comparator for serialized credit lists of the form
/// <c>List&lt;Dictionary&lt;string, string&gt;&gt;</c> with Name / Role / Type
/// keys. Equality mirrors how Jellyfin's people repository keys a credit:
/// trimmed, case-insensitive name and role, plus the person type, in list
/// order.
/// </summary>
// A plain JSON compare can never settle here. Jellyfin's UpdatePeople matches
// an incoming credit to an existing row case-insensitively and then keeps the
// existing row's name and role text, trims both, and drops duplicate credits.
// So a source "Jean-Claude van Damme" written over a local "Jean-Claude Van
// Damme" leaves local unchanged, an exact compare reports a diff on the very
// next refresh, and the row re-queues forever.
public sealed class PeopleListComparator : ISyncComparator<string>
{
    private const string UnknownType = "Unknown";

    /// <inheritdoc />
    public bool Equals(string? source, string? local)
        => DescribeDifference(source, local) == null;

    /// <summary>
    /// Returns the first detected mismatch between two credit lists, or null
    /// when they match.
    /// </summary>
    public string? DescribeDifference(string? source, string? local)
    {
        var sourceKeys = Normalize(source);
        var localKeys = Normalize(local);

        if (sourceKeys == null || localKeys == null)
        {
            return string.Equals(source, local, StringComparison.Ordinal) ? null : "people blob parse error";
        }

        if (sourceKeys.Count != localKeys.Count)
        {
            return $"source has {sourceKeys.Count} credit(s), local has {localKeys.Count}";
        }

        for (var i = 0; i < sourceKeys.Count; i++)
        {
            if (!string.Equals(sourceKeys[i], localKeys[i], StringComparison.Ordinal))
            {
                return $"credit [{i}]: source {sourceKeys[i]}, local {localKeys[i]}";
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? ComputeHash(string? value)
    {
        var keys = Normalize(value);
        if (keys == null || keys.Count == 0)
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", keys)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Reduces a blob to its ordered, de-duplicated credit keys. Null on a
    /// parse failure, empty for a null or empty blob.
    /// </summary>
    private static List<string>? Normalize(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return new List<string>();
        }

        List<Dictionary<string, string>>? people;
        try
        {
            people = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(blob);
        }
        catch (JsonException)
        {
            return null;
        }

        if (people == null)
        {
            return new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>(people.Count);
        foreach (var person in people)
        {
            if (person == null || !person.TryGetValue("Name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            person.TryGetValue("Role", out var role);
            person.TryGetValue("Type", out var type);

            var key = string.Join(
                "|",
                name.Trim().ToLowerInvariant(),
                (string.IsNullOrWhiteSpace(type) ? UnknownType : type.Trim()).ToLowerInvariant(),
                (role ?? string.Empty).Trim().ToLowerInvariant());

            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
