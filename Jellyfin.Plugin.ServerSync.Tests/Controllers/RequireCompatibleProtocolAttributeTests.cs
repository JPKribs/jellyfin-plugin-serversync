using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The filter lets a peer route run only for a caller whose protocol overlaps this server's, and answers any other with 426.
/// </summary>
public sealed class RequireCompatibleProtocolAttributeTests
{
    private const string ThisVersion = "0.0.0-test";

    /// <summary>
    /// A protocol header that is not a whole number is read as missing, so the plugin version decides.
    /// True: a garbled protocol passes only on exactly this plugin version.
    /// False: a garbled header would be let through, or would refuse a server on the same version.
    /// </summary>
    [Theory]
    [InlineData("one", ThisVersion, true)]
    [InlineData("1.5", ThisVersion, true)]
    [InlineData("", ThisVersion, true)]
    [InlineData("one", "9.9.9.9", false)]
    [InlineData("one", null, false)]
    public void OnActionExecuting_ProtocolNotANumber_FallsBackToTheExactVersion(string protocol, string? version, bool allowed)
    {
        var context = Context(version, protocol, null);

        new RequireCompatibleProtocolAttribute().OnActionExecuting(context);

        Assert.Equal(allowed, context.Result is null);
    }

    /// <summary>
    /// A protocol with no minimum is read as speaking that one protocol only.
    /// True: this server's protocol passes alone, and a newer one with no stated floor is refused.
    /// False: a newer peer that dropped this protocol would be let through.
    /// </summary>
    [Theory]
    [InlineData(HintProtocol.ProtocolVersion, true)]
    [InlineData(HintProtocol.ProtocolVersion + 1, false)]
    [InlineData(HintProtocol.MinProtocolVersion - 1, false)]
    [InlineData(int.MaxValue, false)]
    public void OnActionExecuting_ProtocolWithoutAMinimum_MustBeThisProtocol(int protocol, bool allowed)
    {
        var context = Context("9.9.9.9", protocol.ToString(CultureInfo.InvariantCulture), null);

        new RequireCompatibleProtocolAttribute().OnActionExecuting(context);

        Assert.Equal(allowed, context.Result is null);
    }

    /// <summary>
    /// A newer peer that still speaks this server's protocol is let through.
    /// True: servers on different releases keep syncing while their protocols overlap.
    /// False: every release would cut off every peer until all were updated together.
    /// </summary>
    [Fact]
    public void OnActionExecuting_NewerPeerThatStillSpeaksThisProtocol_IsLetThrough()
    {
        var context = Context("9.9.9.9", (HintProtocol.ProtocolVersion + 5).ToString(CultureInfo.InvariantCulture), HintProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture));

        new RequireCompatibleProtocolAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    /// <summary>
    /// A refusal is a 426 whose body is the mismatch in words.
    /// True: the caller pauses the peer and shows the operator which versions differ.
    /// False: the caller would retry forever with no reason to show.
    /// </summary>
    [Fact]
    public void OnActionExecuting_Refused_Answers426WithTheReason()
    {
        var context = Context("9.9.9.9", null, null);

        new RequireCompatibleProtocolAttribute().OnActionExecuting(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(HintProtocol.VersionMismatchStatus, result.StatusCode);
        Assert.Equal(HintProtocol.ProtocolMismatch(null, null, "9.9.9.9", ThisVersion), result.Value);
    }

    /// <summary>
    /// The filter refuses to run without a context.
    /// True: a caller's mistake is named at once.
    /// False: a null context would surface as an unrelated null reference.
    /// </summary>
    [Fact]
    public void OnActionExecuting_NullContext_Throws()
    {
        var attribute = new RequireCompatibleProtocolAttribute();

        Assert.Throws<ArgumentNullException>(() => attribute.OnActionExecuting(null!));
    }

    /// <summary>
    /// The attribute goes on an action only.
    /// True: Capabilities stays open to any protocol, since it is how a peer learns this server's.
    /// False: set on a controller, it would refuse the one route a mismatched peer needs.
    /// </summary>
    [Fact]
    public void Usage_Always_IsMethodOnly()
    {
        var usage = typeof(RequireCompatibleProtocolAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
    }

    private static ActionExecutingContext Context(string? version, string? protocol, string? minProtocol)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginConfigurationManager>(new FakeConfigManager { PluginVersion = ThisVersion });
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (version is not null)
        {
            http.Request.Headers[HintProtocol.VersionHeader] = version;
        }

        if (protocol is not null)
        {
            http.Request.Headers[HintProtocol.ProtocolHeader] = protocol;
        }

        if (minProtocol is not null)
        {
            http.Request.Headers[HintProtocol.MinProtocolHeader] = minProtocol;
        }

        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }
}
