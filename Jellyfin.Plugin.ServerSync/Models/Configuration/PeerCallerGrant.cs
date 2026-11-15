using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// One Whitelist entry, a user of this server whose key a peer calls with, and the locations those calls may come from.
/// </summary>
public class PeerCallerGrant
{
    /// <summary>The user id that stands for every call made with an API key.</summary>
    public const string ApiKeys = "apikey";

    /// <summary>Gets or sets the id of the user the entry applies to, as 32 lowercase hex characters, or <see cref="ApiKeys"/>.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the user.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the addresses, ranges, or URLs the calls may come from. Empty allows every address.</summary>
    public List<string> Locations { get; set; } = new();
}
