using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The filter behind <see cref="PeerCallerGuardAttribute"/>, which asks <see cref="PeerCallerPolicy"/> about the caller and answers 403 when it refuses.
/// </summary>
public sealed class PeerCallerGuard : IAsyncActionFilter
{
    /// <summary>The request item that carries the user a standard user's key is scoped to.</summary>
    public const string ScopedUserKey = "ServerSync.ScopedUser";

    /// <summary>Gets the user the caller's key is scoped to, when the guard set one.</summary>
    /// <param name="context">The request the guard ran on.</param>
    /// <returns>The user's id, or null when the key is not scoped.</returns>
    public static Guid? ScopedUser(HttpContext context)
        => context?.Items.TryGetValue(ScopedUserKey, out var value) == true && value is Guid id ? id : null;

    private readonly PeerCallerPolicy _policy;
    private readonly ILogger<PeerCallerGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="PeerCallerGuard"/> class.</summary>
    /// <param name="policy">Decides whether a caller may use the sync routes.</param>
    /// <param name="logger">Writes log entries.</param>
    public PeerCallerGuard(PeerCallerPolicy policy, ILogger<PeerCallerGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var remote = http.GetNormalizedRemoteIP();
        var verdict = await _policy.CheckAsync(remote, http.User, http.RequestAborted).ConfigureAwait(false);
        if (verdict.Refusal is not null)
        {
            _logger.LogWarning(
                "Refused a Server Sync call to {Path} from {User} at {Address}: {Reason}",
                JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(http.Request.Path.Value ?? string.Empty, 300),
                JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(http.User.Identity?.Name ?? "an unknown user", 100),
                remote,
                JPKribs.Jellyfin.Base.FormatUtilities.SanitizeForLog(verdict.Refusal, 300));
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
