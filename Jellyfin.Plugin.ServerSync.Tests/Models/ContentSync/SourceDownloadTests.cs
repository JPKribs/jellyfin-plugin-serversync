using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.ContentSync;

/// <summary>
/// A download resumes only against a validator that proves the file on the source is the same one.
/// </summary>
public sealed class SourceDownloadTests
{
    private static readonly DateTimeOffset Modified = new(2026, 1, 29, 17, 30, 45, TimeSpan.Zero);

    private static HttpResponseMessage Response(EntityTagHeaderValue? etag, DateTimeOffset? modified)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        response.Headers.ETag = etag;
        response.Content.Headers.LastModified = modified;
        return response;
    }

    /// <summary>
    /// A strong ETag is the validator, quotes included, even when a date is also given.
    /// True: a resume is tied to the exact bytes the source served.
    /// False: a resume would use the weaker date and could join two different files.
    /// </summary>
    [Fact]
    public void ValidatorOf_StrongETag_ReturnsTheETag()
    {
        using var response = Response(new EntityTagHeaderValue("\"abc123\""), Modified);

        var validator = SourceDownload.ValidatorOf(response);

        Assert.Equal("\"abc123\"", validator);
    }

    /// <summary>
    /// A weak ETag cannot tie a range, so the date stands in.
    /// True: the resume condition is one the source will honor.
    /// False: a weak ETag would be sent in If-Range and the source would restart every resume.
    /// </summary>
    [Fact]
    public void ValidatorOf_WeakETag_FallsBackToTheDate()
    {
        using var response = Response(new EntityTagHeaderValue("\"abc123\"", isWeak: true), Modified);

        var validator = SourceDownload.ValidatorOf(response);

        Assert.Equal("Thu, 29 Jan 2026 17:30:45 GMT", validator);
    }

    /// <summary>
    /// A weak ETag with no date, and an answer with neither, give no validator.
    /// True: the download is marked as one that cannot be resumed safely.
    /// False: a partial file would be resumed against a file that may have changed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidatorOf_NothingStrongAndNoDate_ReturnsNull(bool weakETag)
    {
        using var response = Response(weakETag ? new EntityTagHeaderValue("\"w\"", isWeak: true) : null, null);

        var validator = SourceDownload.ValidatorOf(response);

        Assert.Null(validator);
    }

    /// <summary>
    /// A missing response is refused.
    /// True: a caller that lost its response fails at once with the argument named.
    /// False: the failure would surface later as a null reference with no cause.
    /// </summary>
    [Fact]
    public void ValidatorOf_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SourceDownload.ValidatorOf(null!));
    }

    /// <summary>
    /// A recorded strong ETag turns back into an If-Range condition on that ETag.
    /// True: the resume asks for the rest of the same file.
    /// False: the resume would go out with no condition and append to the wrong bytes.
    /// </summary>
    [Fact]
    public void TryParseIfRange_StrongETag_BuildsAnETagCondition()
    {
        var parsed = SourceDownload.TryParseIfRange("\"abc123\"", out var condition);

        Assert.True(parsed);
        Assert.Equal("\"abc123\"", condition!.EntityTag!.Tag);
        Assert.Null(condition.Date);
    }

    /// <summary>
    /// A recorded date turns back into an If-Range condition on that instant in UTC.
    /// True: the resume carries the same date the source gave.
    /// False: the date would shift by the server's time zone and the source would restart the file.
    /// </summary>
    [Fact]
    public void TryParseIfRange_Date_BuildsADateConditionInUtc()
    {
        var parsed = SourceDownload.TryParseIfRange(Modified.ToString("R", CultureInfo.InvariantCulture), out var condition);

        Assert.True(parsed);
        Assert.Equal(Modified, condition!.Date);
        Assert.Null(condition.EntityTag);
    }

    /// <summary>
    /// A validator that is missing, blank, a broken quote, a weak ETag, or not a date gives no condition.
    /// True: the download starts over when nothing proves the file is the same.
    /// False: a bad header value would throw, or a resume would go out unconditioned.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    [InlineData("abc123")]
    [InlineData("W/\"abc123\"")]
    [InlineData("2026-01-29T17:30:45Z")]
    public void TryParseIfRange_Unusable_ReturnsFalseAndNoCondition(string? validator)
    {
        var parsed = SourceDownload.TryParseIfRange(validator, out var condition);

        Assert.False(parsed);
        Assert.Null(condition);
    }

    /// <summary>
    /// What a response gives as its validator parses back into a condition.
    /// True: every validator the plugin records can be presented on the next attempt.
    /// False: recorded validators would be useless and no download could ever resume.
    /// </summary>
    [Fact]
    public void TryParseIfRange_ValidatorOfAResponse_RoundTrips()
    {
        using var response = Response(null, Modified);
        var validator = SourceDownload.ValidatorOf(response);

        var parsed = SourceDownload.TryParseIfRange(validator, out var condition);

        Assert.True(parsed);
        Assert.Equal(Modified, condition!.Date);
    }
}
