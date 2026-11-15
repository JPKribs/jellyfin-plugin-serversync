namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// No path is a link. Stands in where links do not matter, such as tests of the copy path.
/// </summary>
public sealed class NoLinks : ILinkRegistry
{
    /// <inheritdoc />
    public string? LinkPathFor(string copiedPath) => null;

    /// <inheritdoc />
    public string? CopiedPathFor(string linkPath) => null;
}
