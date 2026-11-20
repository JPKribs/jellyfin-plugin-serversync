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
/// Guards a peer controller. Refuses a caller that is not on the Whitelist, and tells the action which
/// user a standard user's key is scoped to.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PeerCallerGuardAttribute : TypeFilterAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallerGuardAttribute"/> class.
    /// </summary>
    public PeerCallerGuardAttribute()
        : base(typeof(PeerCallerGuard))
    {
    }
}
