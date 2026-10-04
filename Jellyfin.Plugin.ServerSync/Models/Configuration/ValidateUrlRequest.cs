using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Request to validate a server URL.
/// </summary>
public class ValidateUrlRequest
{
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a private network address is acceptable.</summary>
    public bool AllowPrivateNetwork { get; set; } = true;
}
