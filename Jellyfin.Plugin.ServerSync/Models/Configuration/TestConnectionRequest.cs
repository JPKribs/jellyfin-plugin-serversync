using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Request to test connection to a peer server, or to list what it offers.
/// </summary>
public class TestConnectionRequest
{
    [Required]
    public string ServerUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configured entry the request is about. When the page posts the kept sentinel in
    /// place of the key it never saw, the stored key of this entry is used.
    /// </summary>
    public string? ServerKey { get; set; }

    /// <summary>Gets or sets a value indicating whether a private network address is acceptable.</summary>
    public bool AllowPrivateNetwork { get; set; } = true;

    /// <summary>Gets or sets the id of the user the token was generated for, used for non admin fallbacks.</summary>
    public string? AuthenticatedUserId { get; set; }

    /// <summary>Gets or sets the mode the entry is about to be saved with, so a link check can judge the pairing: Pull, Push, or Sync.</summary>
    public string? Mode { get; set; }
}
