using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// A capped stream reads no more than the size the other server promised, and cannot be seeked or written.
/// </summary>
public class CappedReadStreamTests
{
    /// <summary>
    /// A stream that ends exactly at its cap reads through.
    /// True: a file of the promised size is read in full.
    /// False: a correct image would be refused at its last byte.
    /// </summary>
    [Fact]
    public void Read_UpToTheCap_Succeeds()
    {
        using var capped = new CappedReadStream(new MemoryStream(new byte[10]), 10);
        var buffer = new byte[10];

        var read = capped.Read(buffer, 0, buffer.Length);

        Assert.Equal(10, read);
        Assert.Equal(0, capped.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// A stream that holds more than its cap fails on the read that crosses it.
    /// True: a server sending more than it promised is cut off.
    /// False: a hostile server could fill the disk with an image of any size.
    /// </summary>
    [Fact]
    public void Read_PastTheCap_Throws()
    {
        using var capped = new CappedReadStream(new MemoryStream(new byte[10]), 5);
        var buffer = new byte[5];
        Assert.Equal(5, capped.Read(buffer, 0, buffer.Length));

        Assert.Throws<IOException>(() => capped.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// The async read counts bytes the same way as the sync read.
    /// True: the cap holds for streams copied asynchronously.
    /// False: the cap would be bypassed by every async copy.
    /// </summary>
    [Fact]
    public async Task ReadAsync_PastTheCap_Throws()
    {
        using var capped = new CappedReadStream(new MemoryStream(new byte[10]), 5);
        var buffer = new byte[10];

        await Assert.ThrowsAsync<IOException>(async () => Assert.True(await capped.ReadAsync(buffer.AsMemory(), CancellationToken.None) >= 0));
    }

    /// <summary>
    /// The stream is forward only and read only.
    /// True: seeking, measuring, and writing are refused.
    /// False: a caller could read past the cap by seeking back.
    /// </summary>
    [Fact]
    public void Seek_Length_AndWrite_AreNotSupported()
    {
        using var capped = new CappedReadStream(new MemoryStream(new byte[1]), 1);

        Assert.False(capped.CanSeek);
        Assert.False(capped.CanWrite);
        Assert.Throws<NotSupportedException>(() => capped.Length);
        Assert.Throws<NotSupportedException>(() => capped.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => capped.Write(new byte[1], 0, 1));
    }

    /// <summary>
    /// A missing inner stream is refused.
    /// True: a bad call fails where it is made.
    /// False: the first read would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_WithNoStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CappedReadStream(null!, 1));
    }
}
