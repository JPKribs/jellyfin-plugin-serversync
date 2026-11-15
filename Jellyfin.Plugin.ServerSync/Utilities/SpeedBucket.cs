using System;
using System.Diagnostics;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// A rate in bytes per second that several streams can share. Each read takes its bytes from the
/// bucket, and a read that takes more than the bucket holds waits until the rate has made up the
/// difference. A short burst is let through after the bucket has sat idle, so a player fills its first
/// buffer at once, and it is one burst for everything that shares the bucket, however many requests
/// a player splits its reading into.
/// </summary>
public sealed class SpeedBucket
{
    /// <summary>How many seconds of the rate may be sent at once after the bucket has sat idle.</summary>
    public const double BurstSeconds = 3;

    private readonly Func<TimeSpan> _now;
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _held;
    private TimeSpan _at;

    /// <summary>Initializes a new instance of the <see cref="SpeedBucket"/> class.</summary>
    /// <param name="bytesPerSecond">The rate.</param>
    public SpeedBucket(long bytesPerSecond)
        : this(bytesPerSecond, static () => Stopwatch.GetElapsedTime(0))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SpeedBucket"/> class, for tests.</summary>
    /// <param name="bytesPerSecond">The rate.</param>
    /// <param name="now">The clock.</param>
    public SpeedBucket(long bytesPerSecond, Func<TimeSpan> now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
        _bytesPerSecond = bytesPerSecond;
        _held = bytesPerSecond * BurstSeconds;
        _at = now();
    }

    /// <summary>Gets when the bucket was last taken from, on its own clock.</summary>
    public TimeSpan LastUsed
    {
        get
        {
            lock (_lock)
            {
                return _at;
            }
        }
    }

    /// <summary>Changes the rate, such as when the bandwidth schedule's hours begin.</summary>
    /// <param name="bytesPerSecond">The new rate.</param>
    public void SetRate(long bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        lock (_lock)
        {
            _bytesPerSecond = bytesPerSecond;
        }
    }

    /// <summary>Takes bytes that were just read, and says how long to wait before reading more.</summary>
    /// <param name="bytes">The bytes read.</param>
    /// <returns>The wait, or zero while the bucket still holds bytes.</returns>
    public TimeSpan Take(int bytes)
    {
        lock (_lock)
        {
            var now = _now();
            _held = Math.Min(_bytesPerSecond * BurstSeconds, _held + ((now - _at).TotalSeconds * _bytesPerSecond));
            _at = now;
            _held -= bytes;
            return _held < 0 ? TimeSpan.FromSeconds(-_held / _bytesPerSecond) : TimeSpan.Zero;
        }
    }
}
