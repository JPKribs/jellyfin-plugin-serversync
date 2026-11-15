using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares credit lists the way Jellyfin keys a credit, by trimmed name and role without case, plus the person type, in list order.
/// Jellyfin keeps an existing credit's spelling when it matches without case, so an exact compare would queue the row forever.
/// </summary>
public sealed class PeopleListComparator : ISyncComparator<string>
{
    /// <summary>The person type a credit without one is given.</summary>
    private const string UnknownType = "Unknown";

    /// <inheritdoc />
    public bool Equals(string? source, string? local)
        => DescribeDifference(source, local) == null;

    /// <summary>
    /// Describes the first difference between two credit lists.
    /// </summary>
    /// <param name="source">The source's credit list, as JSON.</param>
    /// <param name="local">This server's credit list, as JSON.</param>
    /// <returns>The first difference in words, or null when they match.</returns>
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
            return $"source has {JPKribs.Jellyfin.Base.ActivityText.Count(sourceKeys.Count, "credit")}, local has {localKeys.Count}";
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
    /// Reduces a blob to its credit keys, in order and without repeats. A blob that does not parse gives null.
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
