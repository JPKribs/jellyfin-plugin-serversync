using System;
using System.IO;
using System.Reflection;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

public class StableSecretProtectionTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "ss-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateProtector_KeepsTheKeyFolderApplicationNameAndPurposeTheCopyUsed()
    {
        // The plugin carried its own copy with these exact values. The base derives them from the
        // plugin name, and a mismatch would make every stored API key unreadable.
        Assert.Equal("Jellyfin.Plugin.ServerSync", Plugin.SecretsName);

        var configDir = TempDir();
        try
        {
            var paths = ConfigPaths.Create(configDir);
            var keyDir = Path.Combine(configDir, "Jellyfin.Plugin.ServerSync.Keys");
            Assert.Equal(keyDir, StableSecretProtection.KeyDirectory(paths, Plugin.SecretsName));

            var secrets = StableSecretProtection.CreateProtector(paths, Plugin.SecretsName, NullLogger.Instance);
            Assert.True(secrets.IsAvailable);
            var stored = secrets.Protect("api-key");
            Assert.True(SecretProtector.IsProtected(stored));

            // Keys must be written to the fixed folder, not a profile path.
            Assert.NotEmpty(Directory.GetFiles(keyDir, "key-*.xml"));

            // A provider built the way the old copy built it reads what the base wrote.
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
                .SetApplicationName("Jellyfin.Plugin.ServerSync");
            using var container = services.BuildServiceProvider();
            var legacy = container.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Jellyfin.Plugin.ServerSync.Secrets.v1");
            Assert.Equal("api-key", legacy.Unprotect(stored["enc:v1:".Length..]));
        }
        finally
        {
            if (Directory.Exists(configDir))
            {
                Directory.Delete(configDir, true);
            }
        }
    }

    [Fact]
    public void Build_ReturnsNull_WhenKeyDirectoryUnusable()
    {
        // A path under an existing file can't be created as a directory.
        var file = Path.GetTempFileName();
        try
        {
            Assert.Null(StableSecretProtection.Build(Path.Combine(file, "keys"), Plugin.SecretsName, NullLogger.Instance));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // IApplicationPaths with only the plugin configurations folder answered.
    public class ConfigPaths : DispatchProxy
    {
        private string _configDir = string.Empty;

        public static IApplicationPaths Create(string configDir)
        {
            var paths = DispatchProxy.Create<IApplicationPaths, ConfigPaths>();
            ((ConfigPaths)paths)._configDir = configDir;
            return paths;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_" + nameof(IApplicationPaths.PluginConfigurationsPath)
                ? _configDir
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
