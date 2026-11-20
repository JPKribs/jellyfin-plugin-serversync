using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// Every HTTP route the plugin exposes sits behind Jellyfin's RequiresElevation policy, except the link route and the pages' strings.
/// True: a caller without an administrator's token or API key is refused on every route.
/// False: a route is reachable without authentication, which must never ship.
/// </summary>
public class AuthorizationTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    // The one exemption. The URL inside a link is called by Jellyfin's own ffmpeg and static stream
    // proxy, which send no token. It answers loopback callers only, with a per link secret, and is
    // covered by LinkRouteTests.
    private static readonly Type LinkRoute = typeof(Jellyfin.Plugin.ServerSync.Api.LinkController);

    // The one anonymous action. It gives the pages their strings, which are not secret.
    private static readonly MethodInfo TranslationsRoute = typeof(Jellyfin.Plugin.ServerSync.Api.ConfigurationController).GetMethod(nameof(Jellyfin.Plugin.ServerSync.Api.ConfigurationController.Translations))!;

    [Fact]
    public void EveryController_RequiresElevation_ExceptThePeerRoutes()
    {
        var peerRoutes = new[] { typeof(Jellyfin.Plugin.ServerSync.Api.PeerPairController), typeof(Jellyfin.Plugin.ServerSync.Api.PeerController) };
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
        foreach (var controller in new[] { typeof(Jellyfin.Plugin.ServerSync.Api.PeerPairController), typeof(Jellyfin.Plugin.ServerSync.Api.PeerController) })
        {
            Assert.NotEmpty(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
            Assert.NotEmpty(controller.GetCustomAttributes<Jellyfin.Plugin.ServerSync.Api.PeerCallerGuardAttribute>(inherit: true));
        }
    }

    [Fact]
    public void NoAction_AllowsAnonymous()
    {
        var controllers = PluginAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t != LinkRoute);
        foreach (var controller in controllers)
        {
            Assert.False(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any(), $"{controller.Name} allows anonymous callers");
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m != TranslationsRoute))
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

    /// <summary>
    /// The Translations action is the only anonymous action on a guarded controller, and it only reads.
    /// True: the strings load on a page before anyone signs in, and nothing else opens up.
    /// False: the action lost its exemption, or another action gained one.
    /// </summary>
    [Fact]
    public void TranslationsRoute_IsTheOnlyAnonymousAction_AndOnlyReads()
    {
        var anonymous = PluginAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t != LinkRoute)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .ToList();
        Assert.Equal(new[] { TranslationsRoute }, anonymous);

        var verbs = TranslationsRoute.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>(inherit: true).SelectMany(a => a.HttpMethods).ToList();
        Assert.Equal(new[] { "GET" }, verbs);
    }
}
