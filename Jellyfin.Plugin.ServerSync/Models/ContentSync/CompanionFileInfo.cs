namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Information about a companion file (external subtitle) for an item.
/// </summary>
public class CompanionFileInfo
{
    /// <summary>Gets or sets the file's path on the source.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's name on the source.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream's language code.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the stream's codec.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is a separate file on the source.</summary>
    public bool IsExternal { get; set; }

    /// <summary>Gets or sets the stream's index within its media source.</summary>
    public int StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the media source the stream belongs to, required by the
    /// <c>/Videos/{item}/{mediaSource}/Subtitles/{index}/Stream.{format}</c>
    /// download route.
    /// </summary>
    public string MediaSourceId { get; set; } = string.Empty;
}
