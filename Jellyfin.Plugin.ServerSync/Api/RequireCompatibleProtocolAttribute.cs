using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// Refuses a peer whose protocol does not overlap this server's.
/// A caller that sends no protocol version is let through only on exactly this plugin version.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireCompatibleProtocolAttribute : ActionFilterAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var thisVersion = context.HttpContext.RequestServices.GetRequiredService<IPluginConfigurationManager>().PluginVersion;
        var headers = context.HttpContext.Request.Headers;
        var peerVersion = headers[HintProtocol.VersionHeader].FirstOrDefault();
        var peerProtocol = ReadInt(headers, HintProtocol.ProtocolHeader);
        var peerMin = ReadInt(headers, HintProtocol.MinProtocolHeader);
        if (!HintProtocol.Compatible(peerProtocol, peerMin, peerVersion, thisVersion))
        {
            context.Result = new ObjectResult(HintProtocol.ProtocolMismatch(peerProtocol, peerMin, peerVersion, thisVersion)) { StatusCode = HintProtocol.VersionMismatchStatus };
        }
    }

    /// <summary>Reads a header as a whole number, or null when it is missing or not one.</summary>
    private static int? ReadInt(IHeaderDictionary headers, string name)
        => int.TryParse(headers[name].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
