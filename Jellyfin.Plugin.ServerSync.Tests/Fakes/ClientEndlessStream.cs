using System;
using System.IO;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A response body of zeros with no declared length, which ends only after a set number of bytes, and counts what was read.
/// </summary>
internal sealed class ClientEndlessStream : Stream
{
    private readonly long _length;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientEndlessStream"/> class.
    /// </summary>
    /// <param name="length">How many bytes the stream gives before it ends.</param>
    public ClientEndlessStream(long length) => _length = length;

    /// <summary>Gets how many bytes were read so far.</summary>
    public long BytesRead { get; private set; }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = (int)Math.Min(count, _length - BytesRead);
        Array.Clear(buffer, offset, take);
        BytesRead += take;
        return take;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
