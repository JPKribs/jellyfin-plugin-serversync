using System;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>The version an object carries, as the detail modals show it.</summary>
public class ObjectVersionDto
{
    /// <summary>Gets or sets the id of the server where the edit was made.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets that server's display name, or this server's name.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Gets or sets when the edit was made, in UTC.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets a value indicating whether the edit was made on this server.</summary>
    public bool IsThisServer { get; set; }
}
