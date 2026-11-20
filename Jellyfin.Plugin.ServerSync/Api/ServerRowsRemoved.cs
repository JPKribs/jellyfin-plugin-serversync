namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>What was removed when a server entry was forgotten.</summary>
public class ServerRowsRemoved
{
    /// <summary>Gets or sets the content rows removed.</summary>
    public int Content { get; set; }

    /// <summary>Gets or sets the history rows removed.</summary>
    public int History { get; set; }

    /// <summary>Gets or sets the metadata rows removed.</summary>
    public int Metadata { get; set; }

    /// <summary>Gets or sets the people rows removed.</summary>
    public int People { get; set; }

    /// <summary>Gets or sets the user rows removed.</summary>
    public int Users { get; set; }

    /// <summary>Gets or sets the queued hints removed.</summary>
    public int Hints { get; set; }
}
