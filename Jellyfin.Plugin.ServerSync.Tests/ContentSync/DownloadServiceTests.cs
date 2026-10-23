using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Downloads against a fake source: the stall watchdog that frees a copy whose source stopped
/// sending mid-body, and the replacement of an existing file with its rollback. The client is the
/// real <see cref="SourceServerClient"/> over a stub handler, so the response stream is read with
/// ResponseHeadersRead exactly as it is against a live server.
/// </summary>
public sealed class DownloadServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _downloadDir;
    private readonly string _libraryDir;

    public DownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        _downloadDir = Path.Combine(_tempDir, "temp");
        _libraryDir = Path.Combine(_tempDir, "Movies", "Title (2020)");
        Directory.CreateDirectory(_downloadDir);
        Directory.CreateDirectory(_libraryDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Serves each request a body built by the test, counting the requests.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<Stream> _body;

        public StubHandler(Func<Stream> body) => _body = body;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_body()) });
        }
    }

    /// <summary>
    /// A body that sends its prefix, then either ends, stops sending forever (waiting only on the
    /// read's cancellation, the way a socket that went quiet does), or sends a byte every interval.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly bool _stallAfterPrefix;
        private readonly int _trickleBytes;
        private readonly TimeSpan _trickleInterval;
        private readonly Action? _onEnd;
        private int _position;
        private int _trickled;

        public ScriptedStream(byte[] prefix, bool stallAfterPrefix = false, int trickleBytes = 0, TimeSpan trickleInterval = default, Action? onEnd = null)
        {
            _prefix = prefix;
            _stallAfterPrefix = stallAfterPrefix;
            _trickleBytes = trickleBytes;
            _trickleInterval = trickleInterval;
            _onEnd = onEnd;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            if (_stallAfterPrefix)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (_trickled < _trickleBytes)
            {
                await Task.Delay(_trickleInterval, cancellationToken).ConfigureAwait(false);
                buffer.Span[0] = (byte)'t';
                _trickled++;
                return 1;
            }

            _onEnd?.Invoke();
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Serves one file that drops after <c>dropAfter</c> bytes on its first answer, and honours a
    /// range on later ones while the If-Range validator still matches.
    /// </summary>
    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _file;
        private readonly int _dropAfter;
        private readonly string _etag;

        public RangeHandler(string file, int dropAfter, string etag = "\"v1\"")
        {
            _file = Encoding.ASCII.GetBytes(file);
            _dropAfter = dropAfter;
            _etag = etag;
        }

        public System.Collections.Generic.List<string?> Ranges { get; } = new();

        public string CurrentETag { get; set; } = "\"v1\"";

        public byte[]? ReplacementFile { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            Ranges.Add(range?.From?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var file = ReplacementFile ?? _file;
            var first = Ranges.Count == 1;
            var ifRangeMatches = request.Headers.IfRange?.EntityTag?.ToString() == CurrentETag;
            HttpResponseMessage response;
            if (range?.From is { } from && ifRangeMatches)
            {
                var rest = file.AsMemory((int)from).ToArray();
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(rest) };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, file.Length - 1, file.Length);
            }
            else
            {
                Stream body = first ? new ScriptedStream(file.AsMemory(0, _dropAfter).ToArray(), stallAfterPrefix: true) : new MemoryStream(file);
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
                response.Content.Headers.ContentLength = file.Length;
            }

            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(CurrentETag);
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A download that drops partway is resumed from the bytes already kept.
    /// True: a failure at 90% of a large file costs the last 10%.
    /// False: every retry starts the whole file again.
    /// </summary>
    [Fact]
    public async Task DownloadItem_DroppedPartway_ResumesFromTheKeptBytes()
    {
        var item = Item();
        var handler = new RangeHandler("0123456789", dropAfter: 6);
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://source.test", "key", "test", "0");
        var service = new DownloadService(NullLogger<DownloadService>.Instance) { StallTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config(retries: 2));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("0123456789", File.ReadAllText(item.LocalPath!));
        Assert.Equal(new string?[] { null, "6" }, handler.Ranges);
    }

    /// <summary>
    /// A file that changed on the source between attempts comes back whole.
    /// True: If-Range keeps stale kept bytes from being stitched onto a different file.
    /// False: the result would be half of each version.
    /// </summary>
    [Fact]
    public async Task DownloadItem_SourceChangedBetweenAttempts_StartsOver()
    {
        var item = Item();
        var handler = new RangeHandler("0123456789", dropAfter: 6);
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(new ChangeAfterFirst(handler)), "http://source.test", "key", "test", "0");
        var service = new DownloadService(NullLogger<DownloadService>.Instance) { StallTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config(retries: 2));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("abcdefghij", File.ReadAllText(item.LocalPath!));
    }

    // Swaps the source file and its ETag after the first request.
    private sealed class ChangeAfterFirst : DelegatingHandler
    {
        private readonly RangeHandler _inner;
        private int _calls;

        public ChangeAfterFirst(RangeHandler inner)
            : base(inner)
        {
            _inner = inner;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_calls == 2)
            {
                _inner.CurrentETag = "\"v2\"";
                _inner.ReplacementFile = Encoding.ASCII.GetBytes("abcdefghij");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private static SourceServerClient Client(StubHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://source.test", "key", "test", "0");

    private static PluginConfiguration Config(int retries = 1) => new() { MaxRetryCount = retries, EnableRecyclingBin = false };

    private SyncItem Item(string fileName = "Title (2020).mkv") => new()
    {
        SourceLibraryId = "lib-1",
        LocalLibraryId = "local-1",
        SourceItemId = Guid.NewGuid().ToString("N"),
        SourcePath = "/src/Movies/Title (2020)/" + fileName,
        SourceSize = 0,
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = Path.Combine(_libraryDir, fileName),
        Status = SyncStatus.Queued,
        StatusDate = DateTime.UtcNow
    };

    private string TempFileFor(SyncItem item) => Path.Combine(_downloadDir, FileNameSanitizer.SanitizeTempFileName(item.SourceItemId, item.LocalPath!));

    /// <summary>
    /// A source that sends part of a file and then nothing fails the download as stalled, after the
    /// retry policy tried it again, and leaves the file already in the library alone.
    /// True: a quiet source costs one item, and the run and its Content lock go on.
    /// False: the copy waits forever, since HttpClient's timeout ended with the headers.
    /// </summary>
    [Fact]
    public async Task DownloadItem_SourceStopsSending_FailsAsStalled_AndKeepsExistingFile()
    {
        var item = Item();
        File.WriteAllText(item.LocalPath!, "old version");
        var handler = new StubHandler(() => new ScriptedStream(Encoding.ASCII.GetBytes("partial"), stallAfterPrefix: true));
        using var client = Client(handler);
        var service = new DownloadService(NullLogger<DownloadService>.Instance) { StallTimeout = TimeSpan.FromMilliseconds(300) };

        // The outer token is only a backstop so a regression fails the test instead of hanging it.
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config(retries: 1), cancellationToken: backstop.Token);

        Assert.False(backstop.IsCancellationRequested);
        Assert.False(result.Success);
        Assert.Contains("stalled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("old version", File.ReadAllText(item.LocalPath!));
        Assert.False(File.Exists(TempFileFor(item)));
    }

    /// <summary>
    /// A slow source that keeps sending, with every gap shorter than the stall window but the whole
    /// download far longer, completes.
    /// True: the watchdog measures silence, not total time, so a large file on a slow link finishes.
    /// False: every download longer than the window would be cut off.
    /// </summary>
    [Fact]
    public async Task DownloadItem_SlowButSteadySource_Completes()
    {
        var item = Item();
        // Each gap is a twentieth of the stall window, so a loaded test runner cannot stretch one gap
        // past it, while the whole download still takes longer than the window.
        var handler = new StubHandler(() => new ScriptedStream(Encoding.ASCII.GetBytes("head-"), trickleBytes: 30, trickleInterval: TimeSpan.FromMilliseconds(50)));
        using var client = Client(handler);
        var service = new DownloadService(NullLogger<DownloadService>.Instance) { StallTimeout = TimeSpan.FromSeconds(1) };

        var stopwatch = Stopwatch.StartNew();
        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(1));
        Assert.Equal("head-" + new string('t', 30), File.ReadAllText(item.LocalPath!));
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// Cancelling the run while the source is quiet cancels, rather than reporting a stall.
    /// True: stopping the task stops it, with no retries and no failed row.
    /// False: a cancel would be retried as a stall and recorded as an error.
    /// </summary>
    [Fact]
    public async Task DownloadItem_RunCancelledWhileStalled_ThrowsCancellation()
    {
        var item = Item();
        var handler = new StubHandler(() => new ScriptedStream(Encoding.ASCII.GetBytes("partial"), stallAfterPrefix: true));
        using var client = Client(handler);
        var service = new DownloadService(NullLogger<DownloadService>.Instance) { StallTimeout = TimeSpan.FromMinutes(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config(), cancellationToken: cts.Token));

        Assert.Equal(1, handler.Requests);
        Assert.False(File.Exists(TempFileFor(item)));
    }

    /// <summary>
    /// Replacing a file puts the new download at the path, and every companion the source did not
    /// send back (an nfo, artwork) goes back beside it, with no sidecar left behind.
    /// True: a replaced movie keeps its local metadata.
    /// False: the old companions are lost with the old file, or .replacing sidecars pile up.
    /// </summary>
    [Fact]
    public async Task DownloadItem_ReplacesExistingFile_AndKeepsItsCompanions()
    {
        var item = Item();
        File.WriteAllText(item.LocalPath!, "old version");
        var nfo = Path.Combine(_libraryDir, "Title (2020).nfo");
        File.WriteAllText(nfo, "<movie/>");
        var handler = new StubHandler(() => new ScriptedStream(Encoding.ASCII.GetBytes("new version")));
        using var client = Client(handler);
        var service = new DownloadService(NullLogger<DownloadService>.Instance);

        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("new version", File.ReadAllText(item.LocalPath!));
        Assert.Equal("<movie/>", File.ReadAllText(nfo));
        Assert.Empty(Directory.GetFiles(_libraryDir, "*.replacing.*"));
        Assert.False(File.Exists(TempFileFor(item)));
    }

    /// <summary>
    /// When the downloaded file cannot be put in place, the previous file and its companions are
    /// moved back to their own paths and the download reports the failure.
    /// True: a failed replacement leaves the library exactly as it was.
    /// False: the old file stays in a .replacing sidecar and the title disappears from the library.
    /// </summary>
    [Fact]
    public async Task DownloadItem_ReplacementCannotBePlaced_RollsBackTheOldFileAndCompanions()
    {
        if (OperatingSystem.IsWindows())
        {
            // The failure is staged by unlinking the open temp file, which only POSIX allows.
            return;
        }

        var item = Item();
        File.WriteAllText(item.LocalPath!, "old version");
        var nfo = Path.Combine(_libraryDir, "Title (2020).nfo");
        File.WriteAllText(nfo, "<movie/>");
        var tempFile = TempFileFor(item);

        // The body ends by removing the finished temp file, so the rename into place fails after
        // the existing file and its companions were moved aside.
        var handler = new StubHandler(() => new ScriptedStream(Encoding.ASCII.GetBytes("new version"), onEnd: () => File.Delete(tempFile)));
        using var client = Client(handler);
        var service = new DownloadService(NullLogger<DownloadService>.Instance);

        var result = await service.DownloadItemAsync(client, item, _downloadDir, 0, false, Config());

        Assert.False(result.Success);
        Assert.Equal("old version", File.ReadAllText(item.LocalPath!));
        Assert.Equal("<movie/>", File.ReadAllText(nfo));
        Assert.Empty(Directory.GetFiles(_libraryDir, "*.replacing.*"));
        Assert.Equal(2, Directory.GetFiles(_libraryDir).Length);
    }
}
