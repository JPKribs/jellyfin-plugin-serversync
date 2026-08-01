using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Write-through stream wrapper that reports the fraction of
/// <c>expectedBytes</c> written so far. Wrapped around the temp-file stream
/// during a download so a single multi-hour file moves the task's progress
/// bar instead of freezing it at the item boundary.
/// <para>
/// Reports are throttled to whole-percent steps — a 50 GB copy writes
/// hundreds of thousands of buffers, and forwarding every one of them to
/// Jellyfin's task-progress plumbing is pure overhead. Does not own the
/// inner stream; the caller's <c>using</c> disposes it.
/// </para>
/// </summary>
public sealed class ProgressReportingStream : Stream
{
    private readonly Stream _inner;
    private readonly long _expectedBytes;
    private readonly IProgress<double> _progress;
    private long _written;
    private int _lastReportedPercent = -1;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="inner">Destination stream; not disposed by this wrapper.</param>
    /// <param name="expectedBytes">Total bytes expected; must be positive.</param>
    /// <param name="progress">Receives the written fraction (0–1) at whole-percent steps.</param>
    public ProgressReportingStream(Stream inner, long expectedBytes, IProgress<double> progress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedBytes);
        _inner = inner;
        _expectedBytes = expectedBytes;
        _progress = progress;
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

    private void Advance(int count)
    {
        _written += count;

        // Sizes drift (stale SourceSize, transfer encoding); never report past 1.
        var fraction = Math.Min(1.0, (double)_written / _expectedBytes);
        var percent = (int)(fraction * 100);
        if (percent > _lastReportedPercent)
        {
            _lastReportedPercent = percent;
            _progress.Report(fraction);
        }
    }
}
