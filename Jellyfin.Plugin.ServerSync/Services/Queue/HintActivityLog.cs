using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using JPKribs.Jellyfin.Base;
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
    private readonly ActivityLogger _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintActivityLog"/> class.
    /// </summary>
    /// <param name="activity">The shared writer for Jellyfin's activity log.</param>
    public HintActivityLog(ActivityLogger activity)
    {
        _activity = activity;
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

    /// <summary>The subject of a hint as a short title, such as "Metadata for Only A (2021)".</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <returns>The subject.</returns>
    public static string Subject(HintKind kind, string? itemPath, string? userName, string key)
        => SubjectOf(kind, NameOf(kind, itemPath, userName, key), userName);

    /// <summary>The subject of a hint around a name already chosen, such as a Jellyfin title.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The item's, person's, or user's name.</param>
    /// <param name="userName">The username, when watch history is concerned.</param>
    /// <returns>The subject.</returns>
    public static string SubjectOf(HintKind kind, string name, string? userName) => kind switch
    {
        HintKind.History => string.IsNullOrEmpty(userName) ? $"Watch history for {name}" : $"Watch history for {name} ({userName})",
        HintKind.Metadata => $"Metadata for {name}",
        HintKind.People => $"Person {name}",
        HintKind.Content => $"File {name}",
        HintKind.Users => $"User {name}",
        _ => name
    };

    /// <summary>
    /// Jellyfin's own title for an item: an episode as its series and episode number, a season as its
    /// series and season, anything else by its name. "Season 1" alone names nothing.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The title.</returns>
    public static string TitleOf(MediaBrowser.Controller.Entities.BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            var series = string.IsNullOrEmpty(episode.SeriesName) ? null : episode.SeriesName;
            var code = EpisodeCode(episode);
            return series is null ? (code is null ? item.Name : $"{code} {item.Name}") : (code is null ? $"{series} {item.Name}" : $"{series} {code}");
        }

        if (item is MediaBrowser.Controller.Entities.TV.Season season && !string.IsNullOrEmpty(season.SeriesName))
        {
            return $"{season.SeriesName} {item.Name}";
        }

        return item.Name;
    }

    /// <summary>The title with the episode's own name after it, for the line of detail.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The line.</returns>
    public static string LineOf(MediaBrowser.Controller.Entities.BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item is MediaBrowser.Controller.Entities.TV.Episode episode && EpisodeCode(episode) is not null && !string.IsNullOrEmpty(episode.SeriesName)
            ? $"{TitleOf(item)} - {item.Name}"
            : TitleOf(item);
    }

    /// <summary>The episode's season and number, as S01E02, or null when either is unknown.</summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The code.</returns>
    public static string? EpisodeCode(MediaBrowser.Controller.Entities.TV.Episode episode)
        => episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "S{0:D2}E{1:D2}", episode.ParentIndexNumber.Value, episode.IndexNumber.Value)
            : null;

    /// <summary>The bare name of what a hint concerns: the file, the person, or the user.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <returns>The name.</returns>
    public static string NameOf(HintKind kind, string? itemPath, string? userName, string key)
    {
        var file = string.IsNullOrEmpty(itemPath) ? null : Path.GetFileNameWithoutExtension(itemPath);
        return kind switch
        {
            HintKind.People or HintKind.Users => userName ?? key,
            _ => file ?? key
        };
    }

    /// <summary>
    /// Records that a hint from a peer was applied here. The entry reads "Synced [title]" and its detail
    /// "[type] [title] was updated from [peer]", with an episode titled by its series and number and
    /// named after it, as "Synced The Show S01E02" over "Metadata for The Show S01E02 - Pilot was
    /// updated from source-a". No full stops, as Jellyfin's own entries have none.
    /// </summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <param name="result">The apply result, which names the local item when the handler knew it.</param>
    /// <returns>A task.</returns>
    public Task AppliedAsync(InboundHint hint, string originName, HintApplyResult result = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        var fallback = NameOf(hint.Kind, hint.ItemPath, hint.UserName, hint.Key);
        return WriteAsync(
            $"Synced {result.ItemTitle ?? fallback}",
            "ServerSync.HintApplied",
            $"{SubjectOf(hint.Kind, result.ItemLine ?? fallback, hint.UserName)} was updated from {originName}",
            LogLevel.Information);
    }

    /// <summary>
    /// Records that a hint from a peer could not apply here and was dropped. The entry reads "Skipped
    /// sync for [name]" and its detail names the type, as "Metadata for Only A (2021) could not be
    /// updated from source-a because the row is ignored on this server". A reason that already names
    /// the type and the name is used as it is.
    /// </summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task DroppedAsync(InboundHint hint, string originName, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hint);
        var name = NameOf(hint.Kind, hint.ItemPath, hint.UserName, hint.Key);
        var subject = SubjectOf(hint.Kind, name, hint.UserName);
        var overview = !string.IsNullOrEmpty(reason) && reason.StartsWith(subject, StringComparison.Ordinal)
            ? reason
            : $"{subject} could not be updated from {originName} because {reason ?? "it could not be applied"}";
        return WriteAsync($"Skipped sync for {name}", "ServerSync.HintDropped", overview, LogLevel.Information);
    }

    /// <summary>Records that a hint from a peer keeps failing here.</summary>
    /// <param name="hint">The hint.</param>
    /// <param name="originName">The peer's display name.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task RetryingAsync(InboundHint hint, string originName, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hint);

        // The first failure is often a passing one. The second and the sixth are worth a line. The rest
        // would repeat the same line hourly, and giving up writes its own.
        if (hint.Attempts is not (1 or 5))
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
    public Task PausedAsync(string peerName, string reason, System.Collections.Generic.IReadOnlyList<OutboundHint> waiting, DateTime until)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var held = waiting.Count == 0 ? string.Empty : $" Waiting to go: {Changes(waiting)}.";
        return WriteAsync(
            Prefix + $"paused changes to {peerName}",
            "ServerSync.PeerPaused",
            $"{Sentence(reason)?.TrimEnd('.')}. Changes to {peerName} wait until {until.ToLocalTime():t} and are then sent again.{held}",
            LogLevel.Warning);
    }

    /// <summary>Records that a peer rejected a whole request, once for the batch.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="count">How many hints the request carried.</param>
    /// <param name="reason">Why.</param>
    /// <returns>A task.</returns>
    public Task RejectedBatchAsync(string peerName, System.Collections.Generic.IReadOnlyList<OutboundHint> rows, string reason)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(
            Prefix + $"{peerName} rejected {rows.Count} change(s)",
            "ServerSync.HintRejected",
            $"{peerName} rejected the request as malformed: {reason}. The changes were {Changes(rows)}, and they are marked failed.",
            LogLevel.Error);
    }

    /// <summary>Records that a peer lost hints and they were sent again.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="count">How many.</param>
    /// <returns>A task.</returns>
    public Task ResentAsync(string peerName, System.Collections.Generic.IReadOnlyList<OutboundHint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(
            Prefix + $"sent {rows.Count} change(s) to {peerName} again",
            "ServerSync.HintResent",
            $"{peerName} had accepted {Changes(rows)} but no longer held them, which happens when it restarts before reporting them done",
            LogLevel.Warning);
    }

    public static string Changes(System.Collections.Generic.IReadOnlyList<OutboundHint> rows, int shown = 5)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var names = new System.Collections.Generic.List<string>();
        foreach (var row in rows)
        {
            var name = Subject(row.Kind, row.ItemPath, row.UserName, row.Key);
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            return "changes";
        }

        var listed = names.Count > shown ? names.GetRange(0, shown) : names;
        var text = listed.Count switch
        {
            1 => listed[0],
            2 => $"{listed[0]} and {listed[1]}",
            _ => string.Join(", ", listed.GetRange(0, listed.Count - 1)) + ", and " + listed[^1]
        };
        return names.Count > shown ? $"{string.Join(", ", listed)}, and {names.Count - shown} more" : text;
    }

    public static string? Sentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    // Never throws, and cuts the text to what the activity log keeps.
    private Task WriteAsync(string name, string type, string? overview, LogLevel severity)
        => _activity.LogAndWaitAsync(name, type, Sentence(overview), severity);
}
