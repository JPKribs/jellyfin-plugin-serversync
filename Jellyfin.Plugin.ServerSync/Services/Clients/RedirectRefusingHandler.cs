using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Services;

public sealed class RedirectRefusingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 300 or >= 400 || response.Headers.Location is null)
        {
            return response;
        }

        var location = response.Headers.Location;
        var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
        response.Dispose();
        throw new HttpRequestException(
            $"The server at {request.RequestUri?.GetLeftPart(UriPartial.Authority)} redirected to {target.GetLeftPart(UriPartial.Path)}. Server Sync does not follow redirects, so set the server URL to the address it redirects to",
            null,
            (HttpStatusCode)status);
    }
}
