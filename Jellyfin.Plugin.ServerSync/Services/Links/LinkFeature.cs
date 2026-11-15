using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// The <c>Link</c> feature in a peer's capabilities: this server streams its files to at least one
/// server it allows linking for.
/// </summary>
public static class LinkFeature
{
    /// <summary>The name of the feature in a peer's capabilities.</summary>
    public const string Name = "Link";

    /// <summary>Whether this server offers links to anyone.</summary>
    /// <param name="config">The configuration whose server entries are checked.</param>
    /// <returns><c>true</c> when a server entry offers links.</returns>
    public static bool Offered(PluginConfiguration config)
        => config?.Servers?.Any(server => server.OffersLinks) == true;
}
