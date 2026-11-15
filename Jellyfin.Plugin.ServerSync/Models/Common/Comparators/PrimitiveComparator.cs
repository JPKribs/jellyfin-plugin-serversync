using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares simple values with their default equality, and fingerprints one as its invariant text.
/// </summary>
/// <typeparam name="T">The simple type compared, such as a number, a date, or a Guid.</typeparam>
public sealed class PrimitiveComparator<T> : ISyncComparator<T>
{
    /// <inheritdoc />
    public bool Equals(T? source, T? local)
        => EqualityComparer<T?>.Default.Equals(source, local);

    /// <inheritdoc />
    public string? ComputeHash(T? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return value.ToString();
    }
}
