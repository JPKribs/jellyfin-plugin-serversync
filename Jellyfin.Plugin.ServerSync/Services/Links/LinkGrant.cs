using System;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Who a link is being read for, a user or this server's own media probe, and the server it was
/// routed to, or null for the link's own server.
/// </summary>
/// <param name="UserId">The user here the link is read for, or null for the probe.</param>
/// <param name="Probe">Whether the reader is this server's own media probe.</param>
/// <param name="Route">The server the link was routed to, or null for the link's own server.</param>
/// <param name="Download">Whether the user is downloading the file, which the source checks against its download permission.</param>
public sealed record LinkGrant(Guid? UserId, bool Probe, LinkRoute? Route = null, bool Download = false);
