namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Information about disk space availability.
/// </summary>
public class DiskSpaceInfo
{
    /// <summary>Gets or sets the free space on the disk, in bytes.</summary>
    public long FreeBytes { get; set; }

    /// <summary>Gets or sets the size of the disk, in bytes.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the free space that must be kept, in bytes, from Minimum free space.</summary>
    public long RequiredBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the free space reaches <see cref="RequiredBytes"/>.</summary>
    public bool IsSufficient { get; set; }

    /// <summary>Gets or sets the library's root folder the space was measured at.</summary>
    public string Path { get; set; } = string.Empty;
}
