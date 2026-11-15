namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from URL validation.
/// </summary>
public class ValidateUrlResponse
{
    /// <summary>Gets or sets a value indicating whether the URL is acceptable.</summary>
    public bool IsValid { get; set; }

    /// <summary>Gets or sets why the URL was refused, when it was.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the URL in the form it is stored.</summary>
    public string? NormalizedUrl { get; set; }
}
