using System.Globalization;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Helpers for working with Jellyfin SDK media-item DTOs.
/// </summary>
public static class MediaItemUtilities
{
    /// <summary>
    /// Returns the size of the first media source, or 0 when unavailable.
    /// </summary>
    public static long GetItemSize(BaseItemDto item)
        => item.MediaSources is { Count: > 0 } sources ? sources[0].Size ?? 0 : 0;

    /// <summary>
    /// Unwraps a Kiota <c>AdditionalData</c> value to its underlying primitive string,
    /// using <see cref="CultureInfo.InvariantCulture"/> for numeric forms.
    /// </summary>
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
                // Composite nodes (UntypedObject / UntypedArray) have no
                // primitive form. The default ToString() yields the CLR type
                // name, which callers would store as if it were a real value.
                _ => null
            };
        }

        return value.ToString();
    }
}
