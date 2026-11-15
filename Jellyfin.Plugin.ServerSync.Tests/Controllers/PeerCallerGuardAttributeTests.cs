using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The attribute puts the Whitelist guard on a whole peer controller, so no peer route can be added without it.
/// </summary>
public sealed class PeerCallerGuardAttributeTests
{
    /// <summary>
    /// The attribute builds the Whitelist guard as its filter.
    /// True: a controller that carries it is checked by <see cref="PeerCallerGuard"/>.
    /// False: the attribute would attach some other filter, or none, and the routes would be open.
    /// </summary>
    [Fact]
    public void Constructor_Always_NamesThePeerCallerGuardAsItsFilter()
    {
        var attribute = new PeerCallerGuardAttribute();

        var filter = attribute.ImplementationType;

        Assert.Equal(typeof(PeerCallerGuard), filter);
        Assert.IsAssignableFrom<TypeFilterAttribute>(attribute);
    }

    /// <summary>
    /// The attribute goes on a class only, once.
    /// True: the guard covers every route of a controller and cannot be set on one action alone.
    /// False: a controller could guard some routes and leave a new one open.
    /// </summary>
    [Fact]
    public void Usage_Always_IsClassOnlyAndSingle()
    {
        var usage = typeof(PeerCallerGuardAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
    }

    /// <summary>
    /// Exactly the two peer controllers carry the guard.
    /// True: every route under Sync/Peer is behind the Whitelist, and the dashboard routes are not.
    /// False: a peer controller lost its guard, or the guard would refuse the operator's own dashboard.
    /// </summary>
    [Fact]
    public void Guard_AcrossThePlugin_SitsOnThePeerControllersOnly()
    {
        var guarded = typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t.GetCustomAttribute<PeerCallerGuardAttribute>(inherit: true) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { typeof(PeerController), typeof(PeerPairController) }, guarded);
    }
}
