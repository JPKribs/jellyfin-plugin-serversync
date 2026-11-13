using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>The content rows to turn back from kept copies into links.</summary>
public sealed class UnkeepRequest
{
    /// <summary>Gets the row ids.</summary>
    public IList<long> Ids { get; init; } = new List<long>();
}
