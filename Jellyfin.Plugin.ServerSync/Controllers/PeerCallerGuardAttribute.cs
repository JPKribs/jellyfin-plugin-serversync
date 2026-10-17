using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

[AttributeUsage(AttributeTargets.Class)]
public sealed class PeerCallerGuardAttribute : TypeFilterAttribute
{
    public PeerCallerGuardAttribute()
        : base(typeof(PeerCallerGuard))
    {
    }
}

public sealed class PeerCallerGuard : IAsyncActionFilter
{
    public const string ScopedUserKey = "ServerSync.ScopedUser";

    public static Guid? ScopedUser(HttpContext context)
        => context?.Items.TryGetValue(ScopedUserKey, out var value) == true && value is Guid id ? id : null;

    private readonly PeerCallerPolicy _policy;
    private readonly ILogger<PeerCallerGuard> _logger;

    public PeerCallerGuard(PeerCallerPolicy policy, ILogger<PeerCallerGuard> logger)
    {
        _policy = policy;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var verdict = await _policy.CheckAsync(http.GetNormalizedRemoteIP(), http.User, http.RequestAborted).ConfigureAwait(false);
        if (verdict.Refusal is not null)
        {
            _logger.LogWarning("Refused a Server Sync call to {Path} from {User} at {Address}: {Reason}", http.Request.Path, http.User.Identity?.Name ?? "an unknown user", http.GetNormalizedRemoteIP(), verdict.Refusal);
            context.Result = new ObjectResult(new PeerCallerRefusal { Code = verdict.Code.ToString(), Message = verdict.Refusal }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        if (verdict.ScopedUserId is { } scoped)
        {
            http.Items[ScopedUserKey] = scoped;
        }

        await next().ConfigureAwait(false);
    }
}

public sealed class PeerCallerRefusal
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
