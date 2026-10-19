using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// The stall watchdog shared by every copy of a response body read past HttpClient's timeout: media
/// downloads, and the image copies of Metadata, People, and User sync.
/// </summary>
public class StallWatchdogStreamTests
{
    /// <summary>Sends its prefix, then nothing until the read is cancelled, or a byte every interval.</summary>
    private sealed class QuietAfterPrefix : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _trickle;
        private readonly TimeSpan _interval;
        private int _position;
        private int _sent;

        public QuietAfterPrefix(string prefix, int trickle = 0, TimeSpan interval = default)
        {
            _prefix = Encoding.ASCII.GetBytes(prefix);
            _trickle = trickle;
            _interval = interval;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            if (_trickle == 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (_sent < _trickle)
            {
                await Task.Delay(_interval, cancellationToken);
                buffer.Span[0] = (byte)'t';
                _sent++;
                return 1;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A source that goes quiet mid-body fails the copy as stalled once the window passes.
    /// True: an image copy from a peer that stopped sending ends, and the item is reported failed.
    /// False: the copy, and the task holding its module lock, wait forever.
    /// </summary>
    [Fact]
    public async Task CopyAsync_SourceGoesQuiet_FailsAsStalled()
    {
        using var destination = new MemoryStream();
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => StallWatchdogStream.CopyAsync(new QuietAfterPrefix("partial"), destination, TimeSpan.FromMilliseconds(200), backstop.Token));

        Assert.Contains("stalled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(backstop.IsCancellationRequested);
        Assert.Equal("partial", Encoding.ASCII.GetString(destination.ToArray()));
    }

    /// <summary>
    /// A source that keeps sending, each gap inside the window but the whole copy longer than it,
    /// completes.
    /// True: the watchdog measures silence, not total time.
    /// False: every copy longer than the window is cut off.
    /// </summary>
    [Fact]
    public async Task CopyAsync_SlowButSteady_Completes()
    {
        using var destination = new MemoryStream();

        await StallWatchdogStream.CopyAsync(new QuietAfterPrefix("img-", trickle: 6, interval: TimeSpan.FromMilliseconds(100)), destination, TimeSpan.FromMilliseconds(350), CancellationToken.None);

        Assert.Equal("img-tttttt", Encoding.ASCII.GetString(destination.ToArray()));
    }

    /// <summary>
    /// Cancelling the caller's token while the source is quiet cancels, rather than reporting a stall.
    /// True: stopping a task stops it without recording a failure.
    /// False: a cancel is reported as a stalled download.
    /// </summary>
    [Fact]
    public async Task CopyAsync_CallerCancels_ThrowsCancellation()
    {
        using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StallWatchdogStream.CopyAsync(new QuietAfterPrefix("partial"), destination, TimeSpan.FromMinutes(5), cts.Token));
    }
}
