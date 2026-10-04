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

    [Fact]
    public void EveryController_RequiresElevation()
    {
        var controllers = PluginAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(controllers);
        foreach (var controller in controllers)
        {
            var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
            Assert.True(authorize.Count > 0, $"{controller.Name} has no [Authorize] attribute");
            Assert.True(authorize.Any(a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal)), $"{controller.Name} does not require elevation");
        }
    }

    [Fact]
    public void NoAction_AllowsAnonymous()
    {
        var controllers = PluginAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        foreach (var controller in controllers)
        {
            Assert.False(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any(), $"{controller.Name} allows anonymous callers");
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.False(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any(), $"{controller.Name}.{action.Name} allows anonymous callers");
            }
        }
    }
}
