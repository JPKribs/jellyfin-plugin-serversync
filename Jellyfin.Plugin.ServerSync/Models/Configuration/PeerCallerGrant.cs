using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

public class PeerCallerGrant
{
    public const string ApiKeys = "apikey";

    public string UserId { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public List<string> Locations { get; set; } = new();
}
