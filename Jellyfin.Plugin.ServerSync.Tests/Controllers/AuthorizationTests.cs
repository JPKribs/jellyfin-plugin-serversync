using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// Every HTTP route the plugin exposes sits behind Jellyfin's RequiresElevation policy.
/// True: a caller without an administrator's token or API key is refused on every route.
/// False: a route is reachable without authentication, which must never ship.
/// </summary>
public class AuthorizationTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    // The one exemption. The URL inside a link is called by Jellyfin's own ffmpeg and static stream
    // proxy, which send no token. It answers loopback callers only, with a per link secret, and is
    // covered by LinkControllerTests.
    private static readonly Type LinkRoute = typeof(Jellyfin.Plugin.ServerSync.Controllers.LinkController);

    [Fact]
    public void EveryController_RequiresElevation_ExceptThePeerRoutes()
    {
        var peerRoutes = new[] { typeof(Jellyfin.Plugin.ServerSync.Controllers.PeerPairController), typeof(Jellyfin.Plugin.ServerSync.Controllers.PeerController) };
        var controllers = PluginAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && !peerRoutes.Contains(t) && t != LinkRoute).ToList();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
            Assert.True(authorize.Count > 0, $"{controller.Name} has no [Authorize] attribute");
            Assert.True(authorize.Any(a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal)), $"{controller.Name} does not require elevation");
        }
    }

    [Fact]
    public void PeerRoutes_AreGuardedByTheWhitelist()
    {
        foreach (var controller in new[] { typeof(Jellyfin.Plugin.ServerSync.Controllers.PeerPairController), typeof(Jellyfin.Plugin.ServerSync.Controllers.PeerController) })
        {
            Assert.NotEmpty(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
            Assert.NotEmpty(controller.GetCustomAttributes<Jellyfin.Plugin.ServerSync.Controllers.PeerCallerGuardAttribute>(inherit: true));
        }
    }

    [Fact]
    public void NoAction_AllowsAnonymous()
    {
        var controllers = PluginAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t != LinkRoute);
        foreach (var controller in controllers)
        {
            Assert.False(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any(), $"{controller.Name} allows anonymous callers");
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.False(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any(), $"{controller.Name}.{action.Name} allows anonymous callers");
            }
        }
    }

    /// <summary>
    /// The link route is the only anonymous route, and it serves nothing but GET and HEAD.
    /// True: the exemption stays exactly one route that reads.
    /// False: the exemption has grown, or the link route accepts writes.
    /// </summary>
    [Fact]
    public void LinkRoute_IsTheOnlyAnonymousRoute_AndOnlyReads()
    {
        var anonymous = PluginAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .ToList();
        Assert.Equal(new[] { LinkRoute }, anonymous);

        var verbs = LinkRoute.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>(inherit: true))
            .SelectMany(a => a.HttpMethods)
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "GET", "HEAD" }, verbs);
    }
}
