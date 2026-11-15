using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Request to authenticate with a peer server using username and password.
/// </summary>
public class AuthenticateRequest
{
    /// <summary>Gets or sets the Server URL of the server to sign in to.</summary>
    [Required]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the username to sign in with.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password to sign in with. It is sent once and never stored.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a private network address is acceptable.</summary>
    public bool AllowPrivateNetwork { get; set; } = true;
}
