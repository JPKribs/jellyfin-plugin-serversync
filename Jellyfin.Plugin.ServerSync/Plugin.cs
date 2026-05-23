using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Main plugin entry point for Server Sync.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;

        _logger.LogInformation("Server Sync plugin initialized");
    }

    /// <inheritdoc />
    public override string Name => "Server Sync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ebd650b5-6f4c-4ccb-b10d-23dffb3a7286");

    /// <inheritdoc />
    public override string Description => "Sync media from a source Jellyfin server to this server.";

    /// <summary>
    /// Singleton instance, set by the Jellyfin host on construction.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        yield return new PluginPageInfo
        {
            Name = "serversync_sync",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync.html",
            MenuSection = "server",
            DisplayName = "Server Sync",
            EnableInMainMenu = true,
            MenuIcon = "sync"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_settings",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_settings.html"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_settings.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_settings.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_shared.css",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_shared.css"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_shared.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_shared.js"
        };
    }
}
