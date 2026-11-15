namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from authentication attempt.
/// </summary>
public class AuthenticateResponse
{
    /// <summary>Gets or sets a value indicating whether the sign in worked.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// The access token to use for API calls.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The username that was authenticated.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// The user ID of the authenticated user.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets a value indicating whether the signed in user is an administrator there.</summary>
    public bool? IsAdministrator { get; set; }

    /// <summary>Gets or sets a value indicating whether the signed in user may download files there, or null when unknown.</summary>
    public bool? CanDownload { get; set; }

    /// <summary>Gets or sets the result code of the sync access check, such as Allowed or NotInstalled.</summary>
    public string? SyncAccess { get; set; }

    /// <summary>Gets or sets the message that explains <see cref="SyncAccess"/>.</summary>
    public string? SyncAccessMessage { get; set; }

    /// <summary>
    /// Server name returned from the authentication.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Server ID returned from the authentication.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Error message if authentication failed.
    /// </summary>
    public string? Message { get; set; }
}
