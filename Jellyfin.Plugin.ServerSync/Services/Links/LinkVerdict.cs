using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>The source's decision on one link stream, and the item and user it was made for.</summary>
public sealed record LinkVerdict(bool Allowed, string? Reason, BaseItem? Item, Guid? UserId)
{
    /// <summary>Builds a verdict that refuses the stream.</summary>
    /// <param name="reason">Why the stream is refused.</param>
    /// <returns>The verdict.</returns>
    public static LinkVerdict Refuse(string reason) => new(false, reason, null, null);
}
