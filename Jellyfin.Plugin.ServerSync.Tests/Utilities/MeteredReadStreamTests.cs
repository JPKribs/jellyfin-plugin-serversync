using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// A metered stream charges every read to an allowance and fails the read once the allowance refuses it.
/// </summary>
public class MeteredReadStreamTests
{
    /// <summary>
    /// Each read is charged with the bytes it returned.
    /// True: the allowance sees exactly what was read, and an end of stream charges nothing.
    /// False: the allowance would drift from the bytes sent.
    /// </summary>
    [Fact]
    public void Read_ChargesTheBytesRead()
    {
        var charges = new List<int>();
        using var metered = new MeteredReadStream(new MemoryStream(new byte[6]), bytes =>
        {
            charges.Add(bytes);
            return true;
        });
        var buffer = new byte[4];

        Assert.Equal(4, metered.Read(buffer, 0, 4));
        Assert.Equal(2, metered.Read(buffer, 0, 4));
        Assert.Equal(0, metered.Read(buffer, 0, 4));

        Assert.Equal(new[] { 4, 2 }, charges);
    }

    /// <summary>
    /// A refused charge fails the read.
    /// True: a stream that used up its allowance stops with an error.
    /// False: a limited stream would keep reading past its limit.
    /// </summary>
    [Fact]
    public void Read_WhenTheAllowanceRefuses_Throws()
    {
        using var metered = new MeteredReadStream(new MemoryStream(new byte[6]), _ => false);

        Assert.Throws<IOException>(() => metered.Read(new byte[4], 0, 4));
    }

    /// <summary>
    /// The async reads charge the same way, through both overloads.
    /// True: a copy done with ReadAsync is metered like a sync read.
    /// False: async copies would bypass the allowance.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ChargesAndRefusesLikeRead()
    {
        var charged = 0;
        using var metered = new MeteredReadStream(new MemoryStream(new byte[6]), bytes =>
        {
            charged += bytes;
            return charged <= 4;
        });
        var buffer = new byte[4];

        Assert.Equal(4, await metered.ReadAsync(buffer, 0, 4, CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(async () => Assert.True(await metered.ReadAsync(buffer.AsMemory(), CancellationToken.None) >= 0));
        Assert.Equal(6, charged);
    }

    /// <summary>
    /// Seeking passes through to the inner stream, and writing is refused.
    /// True: a player can seek a metered file.
    /// False: seeking a linked file would fail, or a caller could write through the meter.
    /// </summary>
    [Fact]
    public void Seek_PassesThrough_AndWriteIsRefused()
    {
        using var metered = new MeteredReadStream(new MemoryStream(new byte[6]), _ => true);

        metered.Seek(3, SeekOrigin.Begin);

        Assert.True(metered.CanSeek);
        Assert.Equal(3, metered.Position);
        Assert.Equal(6, metered.Length);
        Assert.False(metered.CanWrite);
        Assert.Throws<NotSupportedException>(() => metered.Write(new byte[1], 0, 1));
    }

    /// <summary>
    /// Disposing the meter disposes the inner stream.
    /// True: a file behind a metered stream is released when the stream is.
    /// False: file handles would leak until the process exits.
    /// </summary>
    [Fact]
    public void Dispose_DisposesTheInnerStream()
    {
        var inner = new MemoryStream(new byte[1]);
        var metered = new MeteredReadStream(inner, _ => true);

        metered.Dispose();

        Assert.False(inner.CanRead);
    }

    /// <summary>
    /// A missing stream or allowance is refused.
    /// True: a bad call fails where it is made.
    /// False: the first read would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_WithNothingToWrap_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MeteredReadStream(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => new MeteredReadStream(new MemoryStream(), null!));
    }
}
