namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// This server's links: the .strm files Server Sync wrote in place of a full copy. A link sits where the
/// copy would have gone, under the same name with only the extension changed, so a path that misses
/// here can still find its link. A .strm the user made is never a link.
/// </summary>
public interface ILinkRegistry
{
    /// <summary>
    /// The link standing in for a copy that would sit at <paramref name="copiedPath"/>, or null.
    /// </summary>
    /// <param name="copiedPath">The path the copy would have.</param>
    /// <returns>The link's path, or null.</returns>
    string? LinkPathFor(string copiedPath);

    /// <summary>
    /// The path a full copy would have where <paramref name="linkPath"/> sits, with the source file's
    /// extension, or null when <paramref name="linkPath"/> is not one of this server's links.
    /// </summary>
    /// <param name="linkPath">The link's path.</param>
    /// <returns>The copy's path, or null.</returns>
    string? CopiedPathFor(string linkPath);

    /// <summary>
    /// The link at <paramref name="linkPath"/>, or null when that path is not one of this server's links.
    /// </summary>
    /// <param name="linkPath">The path to look up.</param>
    /// <returns>The link, or null.</returns>
    LinkEntry? Find(string linkPath) => null;

    /// <summary>Forgets what was read, so a link just written or removed is seen on the next lookup.</summary>
    void Invalidate()
    {
    }
}
