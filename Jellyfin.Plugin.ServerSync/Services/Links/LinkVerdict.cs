using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>The source's decision on one link stream, and the item and user it was made for.</summary>
/// <param name="Allowed">Whether the stream may go ahead.</param>
/// <param name="Reason">Why the stream is refused, or null when it is allowed.</param>
/// <param name="Item">The item to stream, or null when refused.</param>
/// <param name="UserId">The user here the stream runs as, or null for a probe or a refusal.</param>
public sealed record LinkVerdict(bool Allowed, string? Reason, BaseItem? Item, Guid? UserId)
{
    /// <summary>Builds a verdict that refuses the stream.</summary>
    /// <param name="reason">Why the stream is refused.</param>
    /// <returns>The verdict.</returns>
    public static LinkVerdict Refuse(string reason) => new(false, reason, null, null);
}
