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

        return Task.CompletedTask;
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
}
