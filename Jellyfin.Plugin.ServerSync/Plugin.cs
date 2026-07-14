using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Main plugin entry point for Server Sync.
/// </summary>
public class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    private readonly ILogger<Plugin> _logger;
    private readonly Lazy<SecretProtector> _secrets;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;

        // Same key directory and purpose as the DI-registered protector, so
        // both resolve the same key ring (file-based, safe to share).
        _secrets = new Lazy<SecretProtector>(() =>
        {
            var keyDirectory = System.IO.Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.ServerSync.Keys");
            var provider = Services.Configuration.StableSecretProtection.Build(keyDirectory, logger);
            return new SecretProtector("Jellyfin.Plugin.ServerSync.Secrets.v1", logger, provider);
        });

        // One-time upgrade: a pre-encryption config holds the key in
        // plaintext, and the core config endpoint returns it verbatim on
        // every settings-page load until something saves. Encrypt at startup
        // instead of waiting. Protect() no-ops when Data Protection is
        // unavailable, so this can't loop or throw the plugin out of load.
        try
        {
            if (!string.IsNullOrEmpty(Configuration.SourceServerApiKey)
                && !SecretProtector.IsProtected(Configuration.SourceServerApiKey))
            {
                var protectedKey = _secrets.Value.Protect(Configuration.SourceServerApiKey);
                if (!string.Equals(protectedKey, Configuration.SourceServerApiKey, StringComparison.Ordinal))
                {
                    Configuration.SourceServerApiKey = protectedKey;
                    lock (Services.Configuration.ConfigurationSaveLock.Sync)
                    {
                        SaveConfiguration();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to encrypt the stored API key at startup; it will be encrypted on the next save");
        }

        _logger.LogInformation("Server Sync plugin initialized");
    }

    /// <summary>
    /// Runs on every save from the settings page (Jellyfin core's plugin
    /// configuration endpoint), which otherwise bypasses all server-side
    /// hygiene: clamps/normalization via SanitizeValues, and at-rest
    /// encryption of the API key. The kept-sentinel posted by the page
    /// resolves to the already-stored secret so it never round-trips.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            incoming.SanitizeValues();
            incoming.SourceServerApiKey = _secrets.Value.ResolveIncoming(
                incoming.SourceServerApiKey, Configuration.SourceServerApiKey);
        }

        lock (Services.Configuration.ConfigurationSaveLock.Sync)
        {
            base.UpdateConfiguration(configuration);
        }
    }

    /// <inheritdoc />
    public override string Name => "Server Sync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ebd650b5-6f4c-4ccb-b10d-23dffb3a7286");

    /// <inheritdoc />
    public override string Description => "Sync media from a source Jellyfin server to this server.";

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
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

        foreach (var page in GetSharedPages("serversync"))
        {
            yield return page;
        }
    }
}
