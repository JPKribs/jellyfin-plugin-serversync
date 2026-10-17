using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Main plugin entry point for Server Sync.
/// </summary>
/// <remarks>
/// Sealed: the constructor calls the virtual <c>SaveConfiguration</c> to
/// encrypt a pre-encryption API key at startup, and a derived override would
/// run against a half-built instance.
/// </remarks>
public sealed class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    // The name the secret key folder, application name and purpose derive from. Changing it makes
    // every stored API key unreadable.
    public const string SecretsName = "Jellyfin.Plugin.ServerSync";

    private readonly ILogger<Plugin> _logger;
    private readonly Lazy<SecretProtector> _secrets;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public bool PredatesWhitelist { get; }

    private static bool ReadPredatesWhitelist(string path, ILogger logger)
    {
        try
        {
            return System.IO.File.Exists(path)
                && !System.IO.File.ReadAllText(path).Contains("<PeerCallerGrants", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the saved Server Sync settings before loading them");
            return false;
        }
    }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        PredatesWhitelist = ReadPredatesWhitelist(ConfigurationFilePath, logger);

        // Built by the same factory as the DI-registered protector, so both resolve the same key ring
        // (file based, safe to share).
        _secrets = new Lazy<SecretProtector>(() => StableSecretProtection.CreateProtector(applicationPaths, SecretsName, logger));

        // The disk space checks are static and called from places with no logger of their own.
        Services.DiskSpaceService.Logger = logger;

        // One-time upgrade: a pre-encryption config holds the key in
        // plaintext, and the core config endpoint returns it verbatim on
        // every settings-page load until something saves. Encrypt at startup
        // instead of waiting. Protect() no-ops when Data Protection is
        // unavailable, so this can't loop or throw the plugin out of load.
        try
        {
            // A configuration written before servers became a list carries one source in the legacy
            // fields. Move it into the list so every code path sees one shape.
            var migrated = Configuration.MigrateLegacyServer();
            if (migrated)
            {
                _logger.LogInformation("Moved the single source server into the server list");
            }

            var encrypted = false;
            foreach (var server in Configuration.Servers)
            {
                if (string.IsNullOrEmpty(server.ApiKey) || SecretProtector.IsProtected(server.ApiKey))
                {
                    continue;
                }

                var protectedKey = _secrets.Value.Protect(server.ApiKey);
                if (!string.Equals(protectedKey, server.ApiKey, StringComparison.Ordinal))
                {
                    server.ApiKey = protectedKey;
                    encrypted = true;
                }
            }

            if (migrated || encrypted)
            {
                lock (Services.Configuration.ConfigurationSaveLock.Sync)
                {
                    SaveConfiguration();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare the stored server list at startup. It will be fixed on the next save");
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
    protected override void OnConfigurationSaving(PluginConfiguration incoming, PluginConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);

        incoming.SanitizeValues();

        // The page posts the kept sentinel in place of a key it never saw. Resolve each entry
        // against the stored entry with the same key, so a server can be renamed or reordered
        // without the operator retyping its API key.
        foreach (var server in incoming.Servers)
        {
            var storedServer = stored.FindServer(server.Key);
            server.ApiKey = _secrets.Value.ResolveIncoming(server.ApiKey, storedServer?.ApiKey ?? string.Empty);
        }
    }

    /// <summary>
    /// Saves from the settings page also take the plugin-wide save lock, so the write cannot
    /// interleave with a task recording a run failure (see ConfigurationSaveLock).
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
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
            Name = "serversync_servers",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_servers.html"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_servers.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_servers.js"
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
