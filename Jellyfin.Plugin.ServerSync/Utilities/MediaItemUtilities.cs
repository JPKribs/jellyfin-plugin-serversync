using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utilities for working with Jellyfin media items.
/// </summary>
public static class MediaItemUtilities
{
    /// <summary>
    /// Extracts the file size from an item's media sources.
    /// </summary>
    /// <param name="item">The media item DTO.</param>
    /// <returns>The file size in bytes, or 0 if not available.</returns>
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
    /// Unwraps a Kiota <c>AdditionalData</c> value to its underlying primitive
    /// representation. Kiota stores deserialized JSON values as
    /// <see cref="UntypedNode"/> subclasses (UntypedString, UntypedBoolean,
    /// etc.) whose default <see cref="object.ToString"/> returns the type name,
    /// not the wrapped value. Calling <c>.ToString()</c> directly on a
    /// <c>BaseItemDto.ProviderIds.AdditionalData</c> entry therefore produces
    /// gibberish like "Microsoft.Kiota.Abstractions.Serialization.UntypedString"
    /// instead of "tt12345" — the cause of perpetual ProviderIds desyncs.
    /// </summary>
    /// <param name="value">An entry value from a Kiota AdditionalData dictionary.</param>
    /// <returns>The string representation of the underlying value, or null.</returns>
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
            // GetValue() on each subclass returns the wrapped CLR primitive.
            // UntypedNull yields null.
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
                _ => node.ToString()
            };
        }

        return value.ToString();
    }
}
