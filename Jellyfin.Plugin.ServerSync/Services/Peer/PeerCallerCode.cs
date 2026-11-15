namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>Why a caller was let in or turned away.</summary>
public enum PeerCallerCode
{
    /// <summary>The caller may use the peer routes.</summary>
    Allowed,

    /// <summary>The caller is a user the Whitelist does not name.</summary>
    UserNotWhitelisted,

    /// <summary>The caller is an administrator or API key whose address the Whitelist does not allow.</summary>
    IpNotWhitelisted,

    /// <summary>The caller's user is on the Whitelist, but not for the address the call came from.</summary>
    UserNotWhitelistedForIp
}
