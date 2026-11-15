using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Remembers what a lookup answered for each key until a run clears it, so a run that asks the same question for every item asks it once.
/// </summary>
/// <typeparam name="TKey">What the lookup is asked about.</typeparam>
/// <typeparam name="TValue">What the lookup answers, including null for no answer.</typeparam>
public sealed class PerRunLookup<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _answers = new();

    /// <summary>
    /// Gets the answer for a key, running the lookup only the first time the key is asked about.
    /// Threads that ask at the same moment share one lookup.
    /// </summary>
    /// <param name="key">What to look up.</param>
    /// <param name="lookup">Answers for a key that has no answer yet.</param>
    /// <returns>The remembered answer.</returns>
    public TValue Get(TKey key, Func<TKey, TValue> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return _answers.GetOrAdd(key, k => new Lazy<TValue>(() => lookup(k))).Value;
    }

    /// <summary>
    /// Forgets every answer.
    /// </summary>
    public void Clear() => _answers.Clear();
}
