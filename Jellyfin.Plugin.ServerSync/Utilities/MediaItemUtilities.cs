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
    {
        if (item.MediaSources != null && item.MediaSources.Count > 0)
        {
            var firstSource = item.MediaSources[0];
            if (firstSource.Size.HasValue)
            {
                return firstSource.Size.Value;
            }
        }

        return 0;
    }

    /// <summary>
    /// Unwraps a Kiota <c>AdditionalData</c> value to its underlying primitive string,
    /// using <see cref="System.Globalization.CultureInfo.InvariantCulture"/> for numeric forms.
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
                UntypedInteger ui => ui.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                UntypedLong ul => ul.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                UntypedDouble ud => ud.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                UntypedDecimal udec => udec.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                UntypedFloat uf => uf.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture),
                UntypedNull => null,
                // Composite nodes (UntypedObject / UntypedArray) have no
                // primitive form; the default ToString() yields the CLR type
                // name, which callers would store as if it were a real value.
                _ => null
            };
        }

        return value.ToString();
    }
}
