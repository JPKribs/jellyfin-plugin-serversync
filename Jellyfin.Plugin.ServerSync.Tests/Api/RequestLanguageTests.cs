using Jellyfin.Plugin.ServerSync.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// RequestLanguage takes the first language of the Accept-Language header.
/// True: a route answers in the language the browser asked for first.
/// False: a route answers in a language the viewer ranked lower, or throws on a missing header.
/// </summary>
public class RequestLanguageTests
{
    [Theory]
    [InlineData("de-DE,de;q=0.9,en;q=0.8", "de-DE")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData(" fr ;q=1", "fr")]
    [InlineData("*", null)]
    [InlineData("", null)]
    public void Of_ReadsTheFirstLanguage(string header, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = header;
        Assert.Equal(expected, RequestLanguage.Of(context.Request));
    }

    [Fact]
    public void Of_NoRequest_IsUsEnglish()
    {
        Assert.Null(RequestLanguage.Of(null));
    }
}
