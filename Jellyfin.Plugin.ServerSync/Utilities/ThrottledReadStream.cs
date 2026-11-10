using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        : this(bytesPerSecond, Stopwatch.GetElapsedTime(0).Duration)
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

/// <summary>
/// A read-through wrapper that holds reads to the rate of a <see cref="SpeedBucket"/>, which other
/// streams may share. It stays seekable, so a response that serves byte ranges from it still can.
/// <para>
/// Owns the inner stream and disposes it.
/// </para>
/// </summary>
public sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly SpeedBucket _bucket;

    /// <summary>Initializes a new instance of the <see cref="ThrottledReadStream"/> class.</summary>
    /// <param name="inner">The stream to read from. Disposed with this wrapper.</param>
    /// <param name="bucket">The rate, shared with any other stream held to it.</param>
    public ThrottledReadStream(Stream inner, SpeedBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bucket);
        _inner = inner;
        _bucket = bucket;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        var wait = _bucket.Take(read);
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        var wait = _bucket.Take(read);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
