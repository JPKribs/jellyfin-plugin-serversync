using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// A read-through wrapper that holds reads to a rate in bytes per second. It stays seekable, so a
/// response that serves byte ranges from it still can. A short burst is let through at the start, so a
/// player fills its first buffer at once and a media probe is not slowed.
/// <para>
/// Owns the inner stream and disposes it.
/// </para>
/// </summary>
public sealed class ThrottledReadStream : Stream
{
    /// <summary>How many seconds of the rate are let through before reads are held back.</summary>
    public const double BurstSeconds = 3;

    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _clock = new();
    private long _read;

    /// <summary>Initializes a new instance of the <see cref="ThrottledReadStream"/> class.</summary>
    /// <param name="inner">The stream to read from. Disposed with this wrapper.</param>
    /// <param name="bytesPerSecond">The most bytes to read each second.</param>
    public ThrottledReadStream(Stream inner, long bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _inner = inner;
        _bytesPerSecond = bytesPerSecond;
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

    /// <summary>How long a read must wait after <paramref name="read"/> bytes in <paramref name="elapsed"/>.</summary>
    /// <param name="read">The bytes read so far.</param>
    /// <param name="elapsed">The time since the first read.</param>
    /// <param name="bytesPerSecond">The rate.</param>
    /// <returns>The wait, or zero when the stream is within its rate.</returns>
    public static TimeSpan DelayFor(long read, TimeSpan elapsed, long bytesPerSecond)
    {
        var due = TimeSpan.FromSeconds(((double)read / bytesPerSecond) - BurstSeconds);
        return due > elapsed ? due - elapsed : TimeSpan.Zero;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var wait = Wait();
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        return Count(_inner.Read(buffer, offset, count));
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var wait = Wait();
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
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

    private TimeSpan Wait()
    {
        _clock.Start();
        return DelayFor(_read, _clock.Elapsed, _bytesPerSecond);
    }

    private int Count(int read)
    {
        _read += read;
        return read;
    }
}
