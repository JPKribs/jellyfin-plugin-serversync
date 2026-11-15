using System;
using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Reads values from the items a source returns.
/// </summary>
public static class MediaItemUtilities
{
    /// <summary>
    /// Reads the size of an item's first media source.
    /// </summary>
    /// <param name="item">The item the source listed.</param>
    /// <returns>The size in bytes, or 0 when the source gave none.</returns>
    public static long GetItemSize(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.MediaSources is { Count: > 0 } sources ? sources[0].Size ?? 0 : 0;
    }

    /// <summary>
    /// Reads a Kiota <c>AdditionalData</c> value as text, with numbers in the invariant culture.
    /// </summary>
    /// <param name="value">The value Kiota parsed.</param>
    /// <returns>The value as text, or null for a null, an object, or an array.</returns>
    public static string? UnwrapKiotaPrimitive(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string s)
        {
            return s;
        }

        if (value is UntypedNode node)
        {
            return node switch
            {
                UntypedString us => us.GetValue(),
                UntypedBoolean ub => ub.GetValue() ? "true" : "false",
                UntypedInteger ui => ui.GetValue().ToString(CultureInfo.InvariantCulture),
                UntypedLong ul => ul.GetValue().ToString(CultureInfo.InvariantCulture),
                UntypedDouble ud => ud.GetValue().ToString(CultureInfo.InvariantCulture),
                UntypedDecimal udec => udec.GetValue().ToString(CultureInfo.InvariantCulture),
                UntypedFloat uf => uf.GetValue().ToString(CultureInfo.InvariantCulture),
                UntypedNull => null,

                // An object or array has no text form, and ToString gives its type name.
                _ => null
            };
        }

        return value.ToString();
    }
}
