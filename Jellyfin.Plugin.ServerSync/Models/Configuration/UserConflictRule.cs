namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// What a user's permissions become when both this server and a server it syncs with changed them
/// since the two last agreed.
/// </summary>
public enum UserConflictRule
{
    /// <summary>The other server's permissions replace this server's.</summary>
    Newest = 0,

    /// <summary>Each permission takes the more generous of the two, so the user ends with everything either server allowed.</summary>
    Permissive = 1,

    /// <summary>Each permission takes the stricter of the two, so the user ends with only what both servers allowed.</summary>
    Restrictive = 2
}
