using System;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Who a link is being read for, a user or this server's own media probe, and the server it was
/// routed to, or null for the link's own server.
/// </summary>
public sealed record LinkGrant(Guid? UserId, bool Probe, LinkRoute? Route = null);
