using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Writes one line to Jellyfin's activity log for each hint that reaches a conclusion, so an operator
/// can see individual changes arriving and leaving on the dashboard's Activity page without opening
/// the server log. A hint that was already satisfied writes nothing, since nothing happened.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HintActivityLog
{
    private const string Prefix = "Server Sync: ";
    private readonly IActivityManager _activity;
    private readonly ILogger<HintActivityLog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintActivityLog"/> class.
    /// </summary>
    /// <param name="activity">Jellyfin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public HintActivityLog(IActivityManager activity, ILogger<HintActivityLog> logger)
    {
        _activity = activity;
        _logger = logger;
    }

    /// <summary>A short description of what a hint concerns, for logs and the dashboard.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <returns>The description.</returns>
    public static string Describe(HintKind kind, string? itemPath, string? userName, string key)
    {
        var file = string.IsNullOrEmpty(itemPath) ? null : Path.GetFileNameWithoutExtension(itemPath);
        return kind switch
        {
            HintKind.History => string.IsNullOrEmpty(userName) ? $"watch history for {file ?? key}" : $"{userName}'s watch history for {file ?? key}",
            HintKind.Metadata => $"metadata for {file ?? key}",
            HintKind.People => $"person {userName ?? key}",
            HintKind.Content => $"file {file ?? key}",
            HintKind.Users => $"user {userName ?? key}",
            _ => key
        };
    }

    /// <summary>Records that a hint from a peer was applied here.</summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <returns>A task.</returns>
    public Task AppliedAsync(InboundHint hint, string originName)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return WriteAsync(
            Prefix + $"applied {Describe(hint.Kind, hint.ItemPath, hint.UserName, hint.Key)} from {originName}",
            "ServerSync.HintApplied",
            hint.ItemPath,
            LogLevel.Information);
    }

    /// <summary>Records that a hint from a peer could not apply here and was dropped.</summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task DroppedAsync(InboundHint hint, string originName, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return WriteAsync(
            Prefix + $"dropped {Describe(hint.Kind, hint.ItemPath, hint.UserName, hint.Key)} from {originName}",
            "ServerSync.HintDropped",
            reason,
            LogLevel.Information);
    }

    /// <summary>Records that a hint from a peer keeps failing here.</summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task RetryingAsync(InboundHint hint, string originName, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hint);

        // The first failure is often a passing one. Later failures are worth a line each.
        if (hint.Attempts < 1)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(
            Prefix + $"could not apply {Describe(hint.Kind, hint.ItemPath, hint.UserName, hint.Key)} from {originName}, will retry",
            "ServerSync.HintRetry",
            reason,
            LogLevel.Warning);
    }

    /// <summary>Records that delivery to a peer is paused.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task PausedAsync(string peerName, string reason)
        => WriteAsync(Prefix + $"paused hints to {peerName}", "ServerSync.PeerPaused", reason, LogLevel.Warning);

    /// <summary>Records that a peer rejected a hint for good.</summary>
    /// <param name="hint">The outbound row.</param>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task RejectedAsync(OutboundHint hint, string peerName, string reason)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return WriteAsync(
            Prefix + $"{peerName} rejected {Describe(hint.Kind, hint.ItemPath, hint.UserName, hint.Key)}",
            "ServerSync.HintRejected",
            reason,
            LogLevel.Error);
    }

    /// <summary>Records that a peer lost hints and they were sent again.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="count">How many.</param>
    /// <returns>A task.</returns>
    public Task ResentAsync(string peerName, int count)
        => WriteAsync(Prefix + $"sent {count} hint(s) to {peerName} again", "ServerSync.HintResent", "the peer accepted them earlier but no longer held them", LogLevel.Warning);

    private async Task WriteAsync(string name, string type, string? overview, LogLevel severity)
    {
        try
        {
            var entry = new ActivityLog(name, type, Guid.Empty)
            {
                ShortOverview = overview is null ? null : Trim(overview),
                LogSeverity = severity
            };
            await _activity.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write an activity log entry: {Name}", name);
        }
    }

    private static string Trim(string text) => text.Length > 500 ? text[..500] : text;
}
