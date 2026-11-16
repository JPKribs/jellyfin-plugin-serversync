using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// The Server Sync plugin. It is sealed because its constructor calls the virtual <c>SaveConfiguration</c>.
/// </summary>
public sealed class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    /// <summary>
    /// The name the secret key folder, application name, and purpose derive from. Changing it makes every stored API Key unreadable.
    /// </summary>
    public const string SecretsName = "Jellyfin.Plugin.ServerSync";

    private readonly ILogger<Plugin> _logger;
    /// <summary>Protects API Keys with the same key ring as the protector registered for injection.</summary>
    private readonly Lazy<SecretProtector> _secrets;
    /// <summary>Set to 1 once a refused save has been logged.</summary>
    private int _refusedSaveLogged;

    /// <summary>
    /// Gets why Server Sync is held, when its database was written by a release this one cannot read, or null.
    /// While it is set nothing is saved, so that release's settings stay for it to convert.
    /// </summary>
    public string? HeldReason { get; }

    /// <summary>
    /// Gets a value indicating whether Server Sync is held for a database an older release wrote. While it is, nothing of the plugin runs.
    /// </summary>
    public static bool Held => Instance?.HeldReason is not null;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Gives the data and cache paths.</param>
    /// <param name="xmlSerializer">Reads and writes the settings file.</param>
    /// <param name="logger">Writes log entries.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;

        // The hold comes first, since a failed settings read saves defaults over the file.
        HeldReason = HoldIfOlderDatabase(applicationPaths.DataPath, ConfigurationFilePath, logger);

        _secrets = new Lazy<SecretProtector>(() => StableSecretProtection.CreateProtector(applicationPaths, SecretsName, logger));

        HistorySyncMergeService.EnabledFields = () => Configuration.GetHistoryFields();

        DiskSpaceService.Logger = logger;

        try
        {
            // A key saved while Data Protection was down is plaintext until protected here.
            var encrypted = false;
            foreach (var server in Configuration.Servers ?? new System.Collections.Generic.List<Models.Configuration.SourceServer>())
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

            if (encrypted)
            {
                lock (Services.Configuration.ConfigurationSaveLock.Sync)
                {
                    SaveConfiguration();
                }
            }
        }
        // No failure here may keep the plugin from loading.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare the stored server list at startup. It will be fixed on the next save");
        }

        _logger.LogInformation("Server Sync plugin initialized");
    }

    /// <summary>
    /// Holds the plugin when the database is from a release this one cannot read, copying that release's settings aside once.
    /// The reason is returned so every save is turned away.
    /// </summary>
    private static string? HoldIfOlderDatabase(string dataPath, string settingsPath, ILogger logger)
    {
        var stored = SyncSchema.StoredVersion(dataPath);
        if (!SyncSchema.IsTooOld(stored))
        {
            return null;
        }

        var reason = new SchemaTooOldException(stored!.Value).Message;
        try
        {
            var copy = settingsPath + ".before-" + SyncSchema.OldestUpgradableRelease;
            if (File.Exists(settingsPath) && !File.Exists(copy))
            {
                File.Copy(settingsPath, copy);
                logger.LogWarning("The Server Sync settings were copied to {Copy} before anything could change them", FormatUtilities.SanitizeForLog(copy));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not copy the Server Sync settings aside: {Reason}", FormatUtilities.SanitizeForLog(ex.Message, 300));
        }

        logger.LogError("{Reason} Its settings are not saved until then.", reason);
        return reason;
    }

    /// <summary>
    /// Writes the settings, unless Server Sync is held for an older database.
    /// Jellyfin also calls this with defaults when the settings file cannot be read, which is the write the hold stops.
    /// </summary>
    /// <param name="config">The settings to write.</param>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        if (HeldReason is not null)
        {
            if (System.Threading.Interlocked.Exchange(ref _refusedSaveLogged, 1) == 0)
            {
                _logger.LogWarning("Server Sync settings were not saved. {Reason}", HeldReason);
            }

            return;
        }

        base.SaveConfiguration(config);
    }

    /// <summary>
    /// Sanitizes the settings and protects each API Key on every save from the settings page, which reaches no other server side check.
    /// A key the page posts as kept resolves to the stored secret.
    /// </summary>
    /// <param name="incoming">The settings being saved.</param>
    /// <param name="stored">The settings already stored.</param>
    protected override void OnConfigurationSaving(PluginConfiguration incoming, PluginConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);
        if (HeldReason is not null)
        {
            throw new InvalidOperationException(HeldReason);
        }

        incoming.SanitizeValues();

        var before = LinkCacheRoot(stored);

        // A cache turned off or moved leaves its folder, which is emptied.
        if (stored.LinkCacheOn && (!incoming.LinkCacheOn || !string.Equals(before, LinkCacheRoot(incoming), StringComparison.Ordinal)))
        {
            try
            {
                Services.Links.LinkCache.WipeFolder(before);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not empty the link cache at {Root}: {Reason}", FormatUtilities.SanitizeForLog(before), FormatUtilities.SanitizeForLog(ex.Message, 300));
            }
        }

        foreach (var server in incoming.Servers)
        {
            // A key posted in protected form is the stored key, kept like the sentinel.
            var incomingKey = SecretProtector.IsProtected(server.ApiKey) ? SecretProtector.KeptSentinel : server.ApiKey;
            server.ApiKey = _secrets.Value.ResolveIncoming(incomingKey, KeptApiKeyFor(server, stored));
        }
    }

    /// <summary>
    /// Finds the stored API Key an incoming entry may keep, which is the same entry's key while its Server URL is unchanged.
    /// An edited address would hand the old server's key to whatever the address names, so the key is dropped.
    /// </summary>
    /// <param name="incoming">The entry being saved.</param>
    /// <param name="stored">The stored settings, which hold the protected keys.</param>
    /// <returns>The stored key in protected form, or empty when there is none to keep.</returns>
    public static string KeptApiKeyFor(SourceServer incoming, PluginConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(stored);
        var storedServer = stored.FindServer(incoming.Key);
        if (storedServer is null || !Utilities.ConfigurationUtilities.SameServerUrl(storedServer.Url, incoming.Url))
        {
            return string.Empty;
        }

        return storedServer.ApiKey ?? string.Empty;
    }

    /// <summary>
    /// Saves from the settings page under the plugin's save lock, so the write cannot interleave with a task recording a run failure.
    /// </summary>
    /// <param name="configuration">The settings that were saved.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        lock (Services.Configuration.ConfigurationSaveLock.Sync)
        {
            base.UpdateConfiguration(configuration);
        }
    }

    /// <summary>Resolves the folder the link cache uses under the given settings.</summary>
    private string LinkCacheRoot(PluginConfiguration config)
        => Services.Links.LinkCache.RootFor(config.LinkCachePath, Path.Combine(ApplicationPaths.CachePath, Services.Links.LinkCache.DefaultFolder));

    /// <summary>
    /// Empties the link cache when the plugin is uninstalled, removing only the cache's own files.
    /// </summary>
    public override void OnUninstalling()
    {
        foreach (var root in new[] { Path.Combine(ApplicationPaths.CachePath, Services.Links.LinkCache.DefaultFolder), LinkCacheRoot(Configuration) })
        {
            try
            {
                Services.Links.LinkCache.WipeFolder(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not empty the link cache at {Root} while uninstalling: {Reason}", FormatUtilities.SanitizeForLog(root), FormatUtilities.SanitizeForLog(ex.Message, 300));
            }
        }

        base.OnUninstalling();
    }

    /// <inheritdoc />
    public override string Name => "Server Sync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ebd650b5-6f4c-4ccb-b10d-23dffb3a7286");

    /// <inheritdoc />
    public override string Description => "Sync media, watch history, metadata, people, user settings, and playlists between Jellyfin servers.";

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
            Name = "serversync_sync_table.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_table.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_content.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_content.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_history.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_history.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_metadata.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_metadata.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_users.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_users.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_people.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_people.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync_queue.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync_queue.js"
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
            Name = "serversync_storage",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_storage.html"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_storage.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_storage.js"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_statistics",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_statistics.html"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_statistics.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_statistics.js"
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
