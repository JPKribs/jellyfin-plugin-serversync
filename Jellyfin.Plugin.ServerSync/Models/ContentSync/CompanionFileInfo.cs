namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Information about a companion file (external subtitle) for an item.
/// </summary>
public class CompanionFileInfo
{
    public string SourcePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? Codec { get; set; }

    public bool IsExternal { get; set; }

    public int StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the media source the stream belongs to — required by the
    /// <c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>
    /// download route.
    /// </summary>
    public string MediaSourceId { get; set; } = string.Empty;
}
