using System;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// A verdict must either let a caller in with its scope, or turn it away with a code and a reason, and never both.
/// </summary>
public sealed class PeerCallerVerdictTests
{
    /// <summary>
    /// An allowing verdict carries the Allowed code, no refusal, and the scope it was given.
    /// True: a caller limited to one user's view stays limited to it.
    /// False: a scoped caller would see the whole library, or an allowed caller would carry a refusal.
    /// </summary>
    [Fact]
    public void Allow_WithAndWithoutScope_CarriesTheScopeAndNoRefusal()
    {
        var user = Guid.NewGuid();

        var scoped = PeerCallerVerdict.Allow(user);
        var whole = PeerCallerVerdict.Allow(null);

        Assert.Equal(PeerCallerCode.Allowed, scoped.Code);
        Assert.Null(scoped.Refusal);
        Assert.Equal(user, scoped.ScopedUserId);
        Assert.Equal(PeerCallerCode.Allowed, whole.Code);
        Assert.Null(whole.ScopedUserId);
    }

    /// <summary>
    /// A refusing verdict carries its code and reason, and no scope.
    /// True: a refused caller is told why and gets no view of the library.
    /// False: a refusal could carry a user scope a careless reader would act on.
    /// </summary>
    [Theory]
    [InlineData(PeerCallerCode.UserNotWhitelisted)]
    [InlineData(PeerCallerCode.IpNotWhitelisted)]
    [InlineData(PeerCallerCode.UserNotWhitelistedForIp)]
    public void Refuse_Code_CarriesTheReasonAndNoScope(PeerCallerCode code)
    {
        var verdict = PeerCallerVerdict.Refuse(code, "not on the whitelist");

        Assert.Equal(code, verdict.Code);
        Assert.Equal("not on the whitelist", verdict.Refusal);
        Assert.Null(verdict.ScopedUserId);
    }

    /// <summary>
    /// Two verdicts with the same parts are equal, and a different scope makes them differ.
    /// True: a cached verdict can be compared by value.
    /// False: the same decision would compare as different each time it is made.
    /// </summary>
    [Fact]
    public void Equality_SamePartsAndDifferentScope_ComparesByValue()
    {
        var user = Guid.NewGuid();

        Assert.Equal(PeerCallerVerdict.Allow(user), PeerCallerVerdict.Allow(user));
        Assert.NotEqual(PeerCallerVerdict.Allow(user), PeerCallerVerdict.Allow(null));
        Assert.Equal(PeerCallerCode.Allowed, default(PeerCallerVerdict).Code);
    }
}
