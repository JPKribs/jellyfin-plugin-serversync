using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for the plugin configuration manager. It holds a configuration the test edits,
/// returns keys unchanged, and counts the saves it was asked for.
/// </summary>
public sealed class FakeConfigManager : IPluginConfigurationManager
{
    private readonly string _tempPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeConfigManager"/> class with an empty configuration.
    /// </summary>
    public FakeConfigManager()
        : this(new PluginConfiguration(), string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeConfigManager"/> class over a configuration.
    /// </summary>
    /// <param name="configuration">The configuration the manager serves.</param>
    public FakeConfigManager(PluginConfiguration configuration)
        : this(configuration, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeConfigManager"/> class with a temp download folder.
    /// </summary>
    /// <param name="tempPath">The folder <see cref="GetTempDownloadPath"/> returns.</param>
    public FakeConfigManager(string tempPath)
        : this(new PluginConfiguration(), tempPath)
    {
    }

    private FakeConfigManager(PluginConfiguration configuration, string tempPath)
    {
        Configuration = configuration;
        _tempPath = tempPath;
    }

    /// <inheritdoc/>
    public PluginConfiguration Configuration { get; }

    /// <inheritdoc/>
    public string LocalServerName => string.Empty;

    /// <summary>
    /// Gets the plugin version the manager reports.
    /// </summary>
    public string PluginVersion { get; init; } = "0.0.0-test";

    /// <summary>
    /// Gets how many times a save was asked for.
    /// </summary>
    public int Saves { get; private set; }

    /// <inheritdoc/>
    public string DecryptApiKey(string protectedKey) => protectedKey;

    /// <inheritdoc/>
    public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

    /// <inheritdoc/>
    public void SaveConfiguration() => Saves++;

    /// <inheritdoc/>
    public string GetTempDownloadPath() => _tempPath;
}
