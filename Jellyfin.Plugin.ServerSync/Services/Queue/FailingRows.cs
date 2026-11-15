namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What has been failing for long enough to tell the administrator about, for one kind of thing.</summary>
/// <param name="Label">What the rows are, such as "file".</param>
/// <param name="Count">How many rows.</param>
/// <param name="Reason">Why one of them failed, as an example.</param>
public sealed record FailingRows(string Label, int Count, string? Reason);
