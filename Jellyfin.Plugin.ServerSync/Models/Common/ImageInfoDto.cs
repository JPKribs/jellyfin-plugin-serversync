namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// DTO for image information including size and dimensions. Lives in
/// <c>Models.Common</c> because the
/// <see cref="Comparators.ImageManifestComparator"/> consumes it; keeping
/// it under <c>Models.MetadataSync</c> created a wrong-direction
/// dependency from <c>Common</c> into a sibling namespace.
/// </summary>
public class ImageInfoDto
{
    /// <summary>
    /// Gets or sets the image type (Primary, Backdrop, Logo, etc.).
    /// </summary>
    public string ImageType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the image index (for types that support multiple images like Backdrop).
    /// </summary>
    public int ImageIndex { get; set; }

    /// <summary>
    /// Gets or sets the image file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the image width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the image height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the image tag (for source server images, used for change detection).
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Gets the formatted file size (e.g., "1.5 MB").
    /// </summary>
    public string FormattedSize
    {
        get
        {
            if (Size < 1024)
            {
                return $"{Size} B";
            }
            else if (Size < 1024 * 1024)
            {
                return $"{Size / 1024.0:F1} KB";
            }
            else
            {
                return $"{Size / (1024.0 * 1024.0):F1} MB";
            }
        }
    }

    /// <summary>
    /// Gets the formatted dimensions (e.g., "1920x1080").
    /// </summary>
    public string FormattedDimensions => $"{Width}x{Height}";
}
