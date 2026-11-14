namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Which server speaks for a thing several servers hold, during a scan.
/// </summary>
public enum SyncPrecedence
{
    /// <summary>
    /// The server that changed it last. A server ranked lower on the Servers tab is heard over the ones
    /// above it when it holds the newer change, and rank decides when nothing says which is newer.
    /// </summary>
    Newest = 0,

    /// <summary>
    /// The highest ranked server that holds it, whatever the others hold. A lower ranked server only
    /// speaks for what the ones above it do not have.
    /// </summary>
    Rank = 1
}
