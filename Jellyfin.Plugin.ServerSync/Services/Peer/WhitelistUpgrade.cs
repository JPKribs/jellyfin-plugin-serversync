using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

public sealed class WhitelistUpgrade : IHostedService
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<WhitelistUpgrade> _logger;

    public WhitelistUpgrade(IPluginConfigurationManager configManager, IUserManager userManager, ILogger<WhitelistUpgrade> logger)
    {
        _configManager = configManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Seed();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not prepare the Whitelist after updating");
        }

        try
        {
            UpgradeLinkRules();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not apply the linking rules to existing servers after updating");
        }

        try
        {
            FoldStreamIntoPush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not turn the Stream servers into Push servers after updating");
        }

        return Task.CompletedTask;
    }

    // Stream gave a server this one's files and told it nothing. Push gives the same, and tells it
    // about changes in the libraries mapped on its entry. Every Stream entry becomes Push.
    private void FoldStreamIntoPush()
    {
        var config = _configManager.Configuration;
        var folded = 0;
        foreach (var server in config.Servers ?? new System.Collections.Generic.List<SourceServer>())
        {
            if (server.Mode == ServerMode.Stream)
            {
                server.Mode = ServerMode.Push;
                folded++;
                _logger.LogInformation("'{Server}' was a Stream server and is now a Push server. It is given the same files and links. Map libraries on its entry to tell it about changes in them as they happen", server.DisplayName);
            }
        }

        if (folded > 0)
        {
            _configManager.SaveConfiguration();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Seed()
    {
        var config = _configManager.Configuration;
        if (config.PeerCallerWhitelistSeeded)
        {
            return;
        }

        config.PeerCallerGrants ??= new List<PeerCallerGrant>();
        if (config.PeerCallerGrants.Count == 0 && Plugin.Instance?.PredatesWhitelist == true)
        {
            config.PeerCallerGrants.Add(new PeerCallerGrant { UserId = PeerCallerGrant.ApiKeys, UserName = "API keys" });
            foreach (var user in _userManager.GetUsers().Where(u => u.HasPermission(PermissionKind.IsAdministrator)))
            {
                config.PeerCallerGrants.Add(new PeerCallerGrant
                {
                    UserId = user.Id.ToString("N", CultureInfo.InvariantCulture),
                    UserName = user.Username
                });
            }

            _logger.LogWarning("Server Sync added API keys and every administrator to the Whitelist with all locations, so servers that pull from this one keep working after the update. Narrow or remove these rows under Settings > Whitelist to lock the sync endpoints down");
        }

        config.PeerCallerWhitelistSeeded = true;
        _configManager.SaveConfiguration();
    }

    // Before 12.4.12.0 a Pull entry could allow linking, and an entry with no user mappings and no
    // fallback streamed as whichever user the linking server named. Now only Stream, Push, and Sync link,
    // and that user is never trusted. A Pull entry that maps no libraries, so reads nothing from its
    // peer, becomes Stream. An entry left with no way to name a user is warned about.
    private void UpgradeLinkRules()
    {
        var config = _configManager.Configuration;
        if (config.LinkRulesUpgraded)
        {
            return;
        }

        foreach (var server in config.Servers ?? new List<SourceServer>())
        {
            if (!server.AllowLinking)
            {
                continue;
            }

            if (server.Mode == ServerMode.Pull)
            {
                if (server.LibraryMappings?.Any(m => m.IsEnabled) == true)
                {
                    _logger.LogWarning("'{Server}' is a Pull server allowed to link here, which is no longer possible, since Pull only reads from it. Set it to Sync, or to Push if this server reads nothing from it", server.DisplayName);
                }
                else
                {
                    server.Mode = ServerMode.Push;
                    _logger.LogInformation("'{Server}' was a Pull server allowed to link here that maps no libraries, so it is now a Push server", server.DisplayName);
                }
            }

            if (string.IsNullOrEmpty(server.LinkFallbackUserId) && server.UserMappings?.Any(m => m.IsEnabled) != true)
            {
                _logger.LogWarning("'{Server}' maps no users, and the user a linking server names is no longer trusted, so its streams are refused. Map its users, or choose a user under Unmapped Users in its Stream step", server.DisplayName);
            }
        }

        config.LinkRulesUpgraded = true;
        _configManager.SaveConfiguration();
    }
}
