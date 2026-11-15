namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// The outcome of Test Connection, with what the server reported about itself and the key.
/// </summary>
public class ConnectionTestResult
{
    /// <summary>Gets or sets a value indicating whether the test reached the server with the key.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the name the server reports.</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets the Jellyfin server id the server reports.</summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the key belongs to an administrator, or null when the test did not get that far.
    /// </summary>
    public bool? IsAdministrator { get; set; }

    /// <summary>Gets or sets the id of the user the key belongs to there.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the name of the user the key belongs to there.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets a value indicating whether the key's user may download files there, or null when unknown.</summary>
    public bool? CanDownload { get; set; }

    /// <summary>Gets or sets the result code of the sync access check, such as Allowed or NotInstalled.</summary>
    public string? SyncAccess { get; set; }

    /// <summary>Gets or sets the message that explains <see cref="SyncAccess"/>.</summary>
    public string? SyncAccessMessage { get; set; }

    /// <summary>Gets or sets the reason the test failed, when it did.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the message shown to the user for this result.
    /// </summary>
    public string? Message { get; set; }
}
