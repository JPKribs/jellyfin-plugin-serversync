namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Result of a connection test containing server info.
/// </summary>
public class ConnectionTestResult
{
    public bool Success { get; set; }

    public string? ServerName { get; set; }

    public string? ServerId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the key belongs to an administrator. Decided by whether the
    /// elevated system info call succeeded. Null when the test did not get that far.
    /// </summary>
    public bool? IsAdministrator { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the user-facing message for the connection test result.
    /// </summary>
    public string? Message { get; set; }
}
