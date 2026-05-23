using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Stream copy with optional rate limiting.
/// </summary>
public static class StreamUtilities
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, sleeping
    /// between reads to enforce <paramref name="bytesPerSecond"/>.
    /// Zero or negative <paramref name="bytesPerSecond"/> disables the limit.
    /// </summary>
    public static async Task CopyWithSpeedLimitAsync(
        Stream source,
        Stream destination,
        long bytesPerSecond,
        CancellationToken cancellationToken = default)
    {
        if (bytesPerSecond <= 0)
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[DefaultBufferSize];
        var stopwatch = Stopwatch.StartNew();
        long totalBytesRead = 0;

        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;

            var expectedTime = (double)totalBytesRead / bytesPerSecond * 1000;
            var actualTime = stopwatch.ElapsedMilliseconds;

            if (actualTime < expectedTime)
            {
                var delay = (int)Math.Min(expectedTime - actualTime, int.MaxValue);
                if (delay > 10)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
