using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// One answer to a file download: the body, where its bytes start in the file, and the validator a
/// later attempt presents to resume it.
/// </summary>
/// <param name="Stream">The body. The caller disposes it.</param>
/// <param name="ContentLength">The body's length, when the source said.</param>
/// <param name="TotalLength">The whole file's length, when known.</param>
/// <param name="Offset">The byte of the file the body starts at. 0 unless the source resumed.</param>
/// <param name="Validator">The strong ETag or Last-Modified date of the file, or null when the source gave neither.</param>
public sealed record SourceDownload(Stream Stream, long? ContentLength, long? TotalLength, long Offset, string? Validator)
{
    /// <summary>Reads the validator a resume can be tied to. A weak ETag cannot be, so the date stands in.</summary>
    /// <param name="response">The answer.</param>
    /// <returns>The validator, or null.</returns>
    public static string? ValidatorOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.ETag is { IsWeak: false } etag)
        {
            return etag.ToString();
        }

        return response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Turns a validator back into the If-Range condition for a resume.</summary>
    /// <param name="validator">The validator.</param>
    /// <param name="condition">The condition.</param>
    /// <returns>True when the validator can be presented.</returns>
    public static bool TryParseIfRange(string? validator, out RangeConditionHeaderValue? condition)
    {
        condition = null;
        if (string.IsNullOrWhiteSpace(validator))
        {
            return false;
        }

        if (validator.StartsWith('"'))
        {
            if (EntityTagHeaderValue.TryParse(validator, out var etag) && !etag.IsWeak)
            {
                condition = new RangeConditionHeaderValue(etag);
                return true;
            }

            return false;
        }

        if (DateTimeOffset.TryParseExact(validator, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            condition = new RangeConditionHeaderValue(date);
            return true;
        }

        return false;
    }
}
