using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// A read-through wrapper that calls back whenever a read returns bytes, so a watchdog can cancel a download whose source stops sending.
/// It does not own the inner stream.
/// </summary>
public sealed class StallWatchdogStream : Stream
{
    /// <summary>
    /// How long a copy may go without receiving a byte before it counts as stalled.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly Stream _inner;
    private readonly Action _onProgress;

    /// <summary>
    /// Initializes a new instance of the <see cref="StallWatchdogStream"/> class.
    /// </summary>
    /// <param name="inner">The stream read from. Not disposed by this wrapper.</param>
    /// <param name="onProgress">Called after every read that returned at least one byte.</param>
    public StallWatchdogStream(Stream inner, Action onProgress)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(onProgress);
        _inner = inner;
        _onProgress = onProgress;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Advance(_inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Advance(_inner.Read(buffer));

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Advance(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Advance(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Copies a response body under a stall watchdog.
    /// </summary>
    /// <param name="source">The body being read.</param>
    /// <param name="destination">Where it is written.</param>
    /// <param name="idleTimeout">How long the source may send nothing.</param>
    /// <param name="cancellationToken">Cancels the copy without reporting a stall.</param>
    /// <returns>A task that completes when the copy does.</returns>
    public static Task CopyAsync(Stream source, Stream destination, TimeSpan idleTimeout, CancellationToken cancellationToken)
        => CopyAsync(source, destination, idleTimeout, static (from, to, token) => from.CopyToAsync(to, token), cancellationToken);

    /// <summary>
    /// Runs a copy under a stall watchdog, since HttpClient's timeout ends once the headers arrive.
    /// A copy that receives nothing for the idle time fails with an IOException, which the retry policy treats as passing.
    /// </summary>
    /// <param name="source">The body being read.</param>
    /// <param name="destination">Where it is written.</param>
    /// <param name="idleTimeout">How long the source may send nothing.</param>
    /// <param name="copy">The copy to run, given the watched source and the watchdog's token.</param>
    /// <param name="cancellationToken">Cancels the copy without reporting a stall.</param>
    /// <returns>A task that completes when the copy does.</returns>
    public static async Task CopyAsync(Stream source, Stream destination, TimeSpan idleTimeout, Func<Stream, Stream, CancellationToken, Task> copy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(copy);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(idleTimeout);
        var watched = new StallWatchdogStream(source, () => stall.CancelAfter(idleTimeout));
        try
        {
            await copy(watched, destination, stall.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Download stalled: no data received for {idleTimeout.TotalSeconds:F0} seconds", ex);
        }
    }

    /// <summary>Calls back when a read returned bytes.</summary>
    private int Advance(int read)
    {
        if (read > 0)
        {
            _onProgress();
        }

        return read;
    }
}
