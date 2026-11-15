using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// A write-through wrapper that reports the fraction of the expected bytes written, in whole percent steps, so one long download moves the task's progress.
/// It does not own the inner stream.
/// </summary>
public sealed class ProgressReportingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _expectedBytes;
    private readonly IProgress<double> _progress;
    private long _written;
    private int _lastReportedPercent = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressReportingStream"/> class.
    /// </summary>
    /// <param name="inner">The stream written to. Not disposed by this wrapper.</param>
    /// <param name="expectedBytes">The total bytes expected. Must be positive.</param>
    /// <param name="progress">Receives the written fraction, from 0 to 1, at whole percent steps.</param>
    /// <param name="alreadyWritten">The bytes already in place before this stream, for a resumed download.</param>
    public ProgressReportingStream(Stream inner, long expectedBytes, IProgress<double> progress, long alreadyWritten = 0)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(alreadyWritten);
        _inner = inner;
        _expectedBytes = expectedBytes;
        _progress = progress;
        _written = alreadyWritten;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Advance(count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Advance(buffer.Length);
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Advance(count);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Advance(buffer.Length);
    }

    /// <summary>Counts written bytes and reports each new whole percent.</summary>
    private void Advance(int count)
    {
        _written += count;

        // The expected size can be stale, so the fraction never passes 1.
        var fraction = Math.Min(1.0, (double)_written / _expectedBytes);
        var percent = (int)(fraction * 100);
        if (percent > _lastReportedPercent)
        {
            _lastReportedPercent = percent;
            _progress.Report(fraction);
        }
    }
}
