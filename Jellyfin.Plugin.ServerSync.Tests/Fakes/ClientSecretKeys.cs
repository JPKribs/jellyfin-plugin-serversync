using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a secret protector that really encrypts, with its keys in a folder the test owns and deletes.
/// </summary>
internal static class ClientSecretKeys
{
    /// <summary>Builds the protector the plugin builds, over a folder of the test's own.</summary>
    /// <param name="configDir">The folder that stands in for Jellyfin's plugin configuration folder.</param>
    /// <returns>A protector whose stored form differs from the plain text.</returns>
    public static SecretProtector Protector(string configDir)
    {
        var (paths, stub) = InterfaceStub.Create<IApplicationPaths>();
        stub.Answer("get_" + nameof(IApplicationPaths.PluginConfigurationsPath), _ => configDir);
        return StableSecretProtection.CreateProtector(paths, Plugin.SecretsName, NullLogger.Instance);
    }
}
