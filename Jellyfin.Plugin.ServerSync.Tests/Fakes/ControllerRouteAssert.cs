using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Checks by reflection that a controller action is routed and guarded the way the security rules require.
/// </summary>
public static class ControllerRouteAssert
{
    /// <summary>
    /// Asserts that an operator action sits behind the RequiresElevation policy, allows no anonymous caller, and answers one verb at one template.
    /// </summary>
    /// <param name="controller">The controller that declares the action.</param>
    /// <param name="action">The action method's name.</param>
    /// <param name="verb">The one HTTP verb the action answers.</param>
    /// <param name="template">The action's route template, or null when it has none.</param>
    public static void Elevated(Type controller, string action, string verb, string? template)
    {
        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.Contains(authorize, a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal));
        Assert.NotNull(controller.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        var method = Route(controller, action, verb, template);
        Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), a => a.Policy != "RequiresElevation");
    }

    /// <summary>
    /// Asserts that an action allows no anonymous caller and answers the given verbs at one template, and returns it.
    /// </summary>
    /// <param name="controller">The controller that declares the action.</param>
    /// <param name="action">The action method's name.</param>
    /// <param name="verbs">The HTTP verbs the action answers, separated by commas.</param>
    /// <param name="template">The action's route template, or null when it has none.</param>
    /// <returns>The action method.</returns>
    public static MethodInfo Route(Type controller, string action, string verbs, string? template)
    {
        var method = Assert.Single(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), m => m.Name == action);
        var routes = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal(
            verbs.Split(',').OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            routes.SelectMany(r => r.HttpMethods).OrderBy(v => v, StringComparer.Ordinal).ToArray());
        Assert.All(routes, r => Assert.Equal(template, r.Template));
        return method;
    }
}
