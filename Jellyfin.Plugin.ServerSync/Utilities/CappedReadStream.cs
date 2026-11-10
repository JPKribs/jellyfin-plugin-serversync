using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// A read-through wrapper that fails once more than a set number of bytes has been read. Wrapped
/// around a body from another server whose size is known, so a server that sends more than it said
/// cannot fill this server's disk.
/// <para>
/// Does not own the inner stream. The caller's <c>using</c> disposes it.
/// </para>
/// </summary>
public sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _most;
    private long _read;

    /// <summary>Initializes a new instance of the <see cref="CappedReadStream"/> class.</summary>
    /// <param name="inner">The stream to read from. Not disposed by this wrapper.</param>
    /// <param name="most">The most bytes that may be read.</param>
    public CappedReadStream(Stream inner, long most)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _most = most;
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
    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        if (_read > _most)
        {
            throw new IOException($"The other server sent more than the {_most} bytes it said it would");
        }

        return read;
    }
}
