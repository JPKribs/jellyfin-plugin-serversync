using System;
using System.IO;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Configuration;

/// <summary>
/// Builds a Data Protection provider whose encrypted secrets survive changes in
/// how the Jellyfin host is launched.
/// <para>
/// By default ASP.NET Data Protection derives its application discriminator from
/// the host's content-root path and stores keys in a per-user profile directory
/// (<c>~/.aspnet/DataProtection-Keys</c>). Both move when the install is updated,
/// switched between the desktop app and a service, or run under Docker, and when
/// either moves, every previously encrypted secret fails to decrypt with "payload
/// was invalid". This pins both: keys live in a fixed directory under the Jellyfin
/// data folder (persisted with the config they protect, including across Docker
/// container rebuilds via the data volume) and the application name is constant.
/// </para>
/// </summary>
public static class StableSecretProtection
{
    // A constant application name keeps the Data Protection discriminator stable
    // regardless of the host's content root.
    private const string ApplicationName = "Jellyfin.Plugin.ServerSync";

    // The folder under the plugin configurations path that holds the key ring, and the purpose the
    // stored secrets are protected under. Changing either makes every stored secret unreadable.
    private const string KeyDirectoryName = "Jellyfin.Plugin.ServerSync.Keys";
    private const string SecretPurpose = "Jellyfin.Plugin.ServerSync.Secrets.v1";

    // The key-ring container manages the on-disk keys for the process lifetime.
    // hold a reference so it is never collected out from under the protector.
    private static IServiceProvider? _keyRingContainer;

    /// <summary>
    /// Builds the protector for stored secrets. The plugin and the DI container each need one, and
    /// both must use the same key folder and purpose or a secret one writes the other cannot read,
    /// so both build it here.
    /// </summary>
    /// <param name="paths">Jellyfin's application paths, or null when unavailable, in which case secrets stay in plaintext.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>The protector.</returns>
    public static SecretProtector CreateProtector(IApplicationPaths? paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var provider = paths is null
            ? null
            : Build(Path.Combine(paths.PluginConfigurationsPath, KeyDirectoryName), logger);
        return new SecretProtector(SecretPurpose, logger, provider);
    }

    /// <summary>
    /// Returns a provider that encrypts with a plugin-managed, launch-independent
    /// key stored under the Jellyfin data folder, or <c>null</c> when that store
    /// can't be created (the caller then degrades to plaintext rather than reaching
    /// outside the data folder for keys).
    /// </summary>
    /// <param name="keyDirectory">Fixed key-storage directory under the data folder.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>A provider to hand to <c>SecretProtector</c>, or <c>null</c> if unavailable.</returns>
    public static IDataProtectionProvider? Build(string keyDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            Directory.CreateDirectory(keyDirectory);
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
                .SetApplicationName(ApplicationName);

#pragma warning disable CA2000 // The container owns the key ring for the process lifetime; kept alive via _keyRingContainer.
            var container = services.BuildServiceProvider();
#pragma warning restore CA2000
            _keyRingContainer = container;
            return container.GetRequiredService<IDataProtectionProvider>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not initialize plugin-managed Data Protection keys at {Path}. Secrets will be stored in plaintext.", keyDirectory);
            return null;
        }
    }
}
