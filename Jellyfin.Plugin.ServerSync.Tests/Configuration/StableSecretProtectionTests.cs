using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

public class StableSecretProtectionTests
{
    private static string TempKeyDir() =>
        Path.Combine(Path.GetTempPath(), "ss-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Build_RoundTripsWithKeysInTheGivenDirectory()
    {
        var dir = TempKeyDir();
        try
        {
            var provider = StableSecretProtection.Build(dir, NullLogger.Instance);
            Assert.NotNull(provider);

            var protector = provider!.CreateProtector("Jellyfin.Plugin.ServerSync.Secrets.v1");
            var encrypted = protector.Protect("api-key");

            Assert.NotEqual("api-key", encrypted);
            Assert.Equal("api-key", protector.Unprotect(encrypted));

            // Keys must be written to the supplied directory, not a profile path.
            Assert.NotEmpty(Directory.GetFiles(dir, "key-*.xml"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
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
            Assert.Null(StableSecretProtection.Build(Path.Combine(file, "keys"), NullLogger.Instance));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
