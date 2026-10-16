using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Controllers;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireSameVersionAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var thisVersion = context.HttpContext.RequestServices.GetRequiredService<IPluginConfigurationManager>().PluginVersion;
        var peerVersion = context.HttpContext.Request.Headers[HintProtocol.VersionHeader].FirstOrDefault();
        if (!HintProtocol.SameVersion(peerVersion, thisVersion))
        {
            context.Result = new ObjectResult(HintProtocol.VersionMismatch(peerVersion, thisVersion)) { StatusCode = HintProtocol.VersionMismatchStatus };
        }
    }
}
