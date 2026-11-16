using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Statistics;

/// <summary>
/// How many link streams this server started for one server that links to it, day by day.
/// </summary>
public class LinkStreamServerDto
{
    /// <summary>
    /// Gets or sets the server entry's key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server entry's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the streams started on each day, in the order of <see cref="LinkStreamsDto.Days"/>.
    /// </summary>
    public IReadOnlyList<int> Streams { get; set; } = new List<int>();
}
