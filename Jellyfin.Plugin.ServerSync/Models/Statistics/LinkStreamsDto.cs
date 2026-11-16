using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Statistics;

/// <summary>
/// The link streams this server started over the last thirty days, for each server that may link to it.
/// </summary>
public class LinkStreamsDto
{
    /// <summary>
    /// Gets or sets the days, oldest first, each as yyyy-MM-dd in this server's time.
    /// </summary>
    public IReadOnlyList<string> Days { get; set; } = new List<string>();

    /// <summary>
    /// Gets or sets one entry for each server that may link to this one, in rank order.
    /// </summary>
    public IReadOnlyList<LinkStreamServerDto> Servers { get; set; } = new List<LinkStreamServerDto>();
}
