using System;
using System.Net;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The exception must carry the peer's status, and must say the API Key was refused only for a 401 or a 403.
/// </summary>
public sealed class PeerRefusedExceptionTests
{
    /// <summary>
    /// Only a 401 or a 403 counts as the key being refused.
    /// True: History tells the operator to fix the key only when the key is the problem.
    /// False: a peer that is down, or a missing route, would be reported as a bad API Key.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.UpgradeRequired, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void KeyRefused_Status_IsTrueOnlyForUnauthorizedAndForbidden(HttpStatusCode status, bool expected)
    {
        var exception = new PeerRefusedException(status, "refused");

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(expected, exception.KeyRefused);
        Assert.Equal("refused", exception.Message);
    }

    /// <summary>
    /// An exception built without a status does not claim the key was refused.
    /// True: an unknown failure is never blamed on the API Key.
    /// False: every refusal with no status would tell the operator to replace a good key.
    /// </summary>
    [Fact]
    public void KeyRefused_NoStatus_IsFalse()
    {
        var inner = new InvalidOperationException("inner");

        var bare = new PeerRefusedException();
        var withMessage = new PeerRefusedException("why");
        var withInner = new PeerRefusedException("why", inner);

        Assert.False(bare.KeyRefused);
        Assert.False(withMessage.KeyRefused);
        Assert.Equal("why", withMessage.Message);
        Assert.Same(inner, withInner.InnerException);
        Assert.Equal(default, withInner.StatusCode);
    }

    /// <summary>
    /// The exception is an invalid operation.
    /// True: callers that treat a failed negotiation as a failed row catch it.
    /// False: a refusal would escape them and stop the run.
    /// </summary>
    [Fact]
    public void Type_IsAnInvalidOperation()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(new PeerRefusedException(HttpStatusCode.Forbidden, "no"));
    }
}
