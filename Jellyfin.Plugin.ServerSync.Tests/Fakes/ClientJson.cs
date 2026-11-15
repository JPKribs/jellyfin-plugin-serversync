using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Writes the JSON a Jellyfin server answers item queries with, so a stub server can feed the real client.
/// </summary>
internal static class ClientJson
{
    /// <summary>Writes one item.</summary>
    /// <param name="id">The item's id.</param>
    /// <param name="type">The item's type name, such as Movie.</param>
    /// <param name="path">The item's path, or null for none.</param>
    /// <param name="name">The item's name, or null for none.</param>
    /// <returns>The item as JSON.</returns>
    public static string Item(Guid id, string type = "Movie", string? path = null, string? name = null)
    {
        var fields = new Dictionary<string, object?> { ["Id"] = id.ToString("N", CultureInfo.InvariantCulture), ["Type"] = type };
        if (path is not null)
        {
            fields["Path"] = path;
        }

        if (name is not null)
        {
            fields["Name"] = name;
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>Writes a page of items.</summary>
    /// <param name="total">The total the server reports, or null to leave it out.</param>
    /// <param name="items">The items, each already JSON.</param>
    /// <returns>The page as JSON.</returns>
    public static string Page(int? total, params string[] items)
        => "{\"Items\":[" + string.Join(",", items) + "]"
            + (total is null ? string.Empty : ",\"TotalRecordCount\":" + total.Value.ToString(CultureInfo.InvariantCulture))
            + "}";

    /// <summary>Writes a page holding a run of movies with fresh ids.</summary>
    /// <param name="count">How many movies the page holds.</param>
    /// <param name="total">The total the server reports, or null to leave it out.</param>
    /// <returns>The page as JSON.</returns>
    public static string PageOf(int count, int? total)
        => Page(total, Enumerable.Range(0, count).Select(_ => Item(Guid.NewGuid())).ToArray());
}
