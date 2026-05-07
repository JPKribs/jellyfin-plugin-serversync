using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Comparator for primitive value types — bool, int, long, DateTime, Guid, etc.
/// Equality uses <see cref="EqualityComparer{T}.Default"/>. Hashing returns the
/// invariant <c>ToString()</c> form for non-null values, which is sufficient
/// to detect "did the value change since last sync" for the
/// <see cref="SyncableValue{T}"/> fast-path.
/// </summary>
/// <typeparam name="T">Primitive or struct type.</typeparam>
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
