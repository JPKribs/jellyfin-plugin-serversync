using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Writes the Change Queue's events to the Activity page.
/// Changes from peers are summed into one entry for each outcome, such as "12 Items Synced", so a busy sync never floods it.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HintActivityLog : IDisposable
{
    private readonly ActivityLogger _activity;
    private readonly ActivitySummary _summary;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintActivityLog"/> class.
    /// </summary>
    /// <param name="activity">Writes entries to the Activity page.</param>
    public HintActivityLog(ActivityLogger activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
        _summary = new ActivitySummary(activity);
    }

    /// <summary>Describes what a hint concerns, for logs and the dashboard.</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
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
            HintKind.Playlists => $"playlist {userName ?? key}",
            _ => key
        };
    }

    /// <summary>Words the subject of a hint as a short title, such as "Metadata for Only A (2021)".</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <returns>The subject.</returns>
    public static string Subject(HintKind kind, string? itemPath, string? userName, string key)
        => SubjectOf(kind, NameOf(kind, itemPath, userName, key), userName);

    /// <summary>Words the subject of a hint around a name already chosen, such as a Jellyfin title.</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
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
        HintKind.Playlists => $"Playlist {name}",
        _ => name
    };

    /// <summary>Picks the bare name of what a hint concerns, which is the file, the person, or the user.</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <returns>The name.</returns>
    public static string NameOf(HintKind kind, string? itemPath, string? userName, string key)
    {
        var file = string.IsNullOrEmpty(itemPath) ? null : Path.GetFileNameWithoutExtension(itemPath);
        return kind switch
        {
            HintKind.People or HintKind.Users or HintKind.Playlists => userName ?? key,
            _ => file ?? key
        };
    }

    /// <summary>
    /// Records that a hint from a peer was applied here, written together with the others as "12 Items Synced".
    /// </summary>
    /// <param name="hint">The hint that was applied.</param>
    /// <param name="originName">The name the entry gives the origin.</param>
    /// <param name="result">How the apply ended.</param>
    /// <returns>A completed task.</returns>
    public Task AppliedAsync(InboundHint hint, string originName, HintApplyResult result = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        _summary.Add(Synced, originName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records that a hint from a peer was dropped here, written together with the others as "3 Items Not Synced".
    /// </summary>
    /// <param name="hint">The hint that was dropped.</param>
    /// <param name="originName">The name the entry gives the origin.</param>
    /// <param name="code">Why it was dropped.</param>
    /// <returns>A completed task.</returns>
    public Task DroppedAsync(InboundHint hint, string originName, HintCode code)
    {
        ArgumentNullException.ThrowIfNull(hint);
        _summary.Add(NotSynced, originName, ReasonOf(code));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records that a hint from a peer keeps failing here, on its second and sixth try, as "4 Items Retrying".
    /// The first failure is often a passing one, so it writes nothing.
    /// </summary>
    /// <param name="hint">The hint that failed, whose attempts decide whether to write.</param>
    /// <param name="originName">The name the entry gives the origin.</param>
    /// <param name="reason">Why it failed, for the server log only.</param>
    /// <returns>A completed task.</returns>
    public Task RetryingAsync(InboundHint hint, string originName, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hint);
        if (hint.Attempts is 1 or 5)
        {
            _summary.Add(Retrying, originName);
        }

        return Task.CompletedTask;
    }

    /// <summary>Records that delivery to a peer is paused.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="reason">Why the peer cannot take changes.</param>
    /// <param name="waiting">The changes waiting to go to the peer.</param>
    /// <param name="until">When delivery resumes.</param>
    /// <returns>A task that ends when the entry is written.</returns>
    public Task PausedAsync(string peerName, string reason, IReadOnlyList<OutboundHint> waiting, DateTime until)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var held = waiting.Count == 0 ? string.Empty : $". {ActivityText.Count(waiting.Count, "change")} wait to go";
        return WriteAsync(
            $"Changes Paused for {peerName}",
            "ServerSync.PeerPaused",
            $"{ActivityText.Sentence(reason)}. Changes are sent to {peerName} again at {until.ToLocalTime():t}{held}",
            LogLevel.Warning);
    }

    /// <summary>Records that a peer rejected a whole request, once for the batch.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="rows">The hints the request carried.</param>
    /// <param name="reason">Why the peer could not read the request.</param>
    /// <returns>A task that ends when the entry is written.</returns>
    public Task RejectedBatchAsync(string peerName, IReadOnlyList<OutboundHint> rows, string reason)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(
            $"Changes Rejected by {peerName}",
            "ServerSync.HintRejected",
            $"{peerName} rejected {ActivityText.Count(rows.Count, "change")} as malformed and they are marked failed: {reason}",
            LogLevel.Error);
    }

    /// <summary>Records that a peer lost hints and they were sent again.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="rows">The hints sent again.</param>
    /// <returns>A task that ends when the entry is written.</returns>
    public Task ResentAsync(string peerName, IReadOnlyList<OutboundHint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(
            $"Changes Resent to {peerName}",
            "ServerSync.HintResent",
            $"{ActivityText.Count(rows.Count, "change")} {(rows.Count == 1 ? "was" : "were")} sent to {peerName} again after it lost them, as happens when it restarts before reporting them done",
            LogLevel.Warning);
    }

    /// <summary>The summed entry for applied changes, written once the queue is quiet and every few minutes in a long burst.</summary>
    private static readonly ActivityOutcome Synced = new(
        "ServerSync.HintsApplied",
        "Synced",
        t => $"{ActivityText.Count(t.Total, "item")} {ActivityText.WasOrWere(t.Total)} synced from {ActivityText.CountedList(t.Sources)}");

    private static readonly ActivityOutcome NotSynced = new(
        "ServerSync.HintsDropped",
        "Not Synced",
        t => $"{ActivityText.Count(t.Total, "item")} from {ActivityText.CountedList(t.Sources)} {ActivityText.WasOrWere(t.Total)} not synced: {ActivityText.CountedList(t.Reasons)}");

    private static readonly ActivityOutcome Retrying = new(
        "ServerSync.HintsRetrying",
        "Retrying",
        t => $"{ActivityText.Count(t.Total, "item")} from {ActivityText.CountedList(t.Sources)} could not be applied yet and will be retried",
        LogLevel.Warning);

    /// <inheritdoc />
    public void Dispose() => _summary.Dispose();

    /// <summary>Writes whatever summaries are waiting.</summary>
    public void Flush() => _summary.Flush();

    /// <summary>Words a short reason a change was not synced, for the summary.</summary>
    /// <param name="code">The code it was dropped with.</param>
    /// <returns>The reason.</returns>
    public static string ReasonOf(HintCode code) => code switch
    {
        HintCode.Filtered => "filtered out",
        HintCode.NotMapped => "not in a mapped library",
        HintCode.Ignored => "ignored here",
        HintCode.ModuleOff => "sync type off",
        HintCode.Priority => "another server provides it",
        HintCode.SourceGone => "gone from the source",
        HintCode.Unreadable => "path not given",
        HintCode.NoLocalItem => "never arrived here",
        HintCode.GaveUp => "failed too often",
        HintCode.Unsupported => "not supported",
        HintCode.Invalid => "invalid",
        _ => "could not apply"
    };

    /// <summary>Lists the subjects of some changes in a sentence, naming at most a few and counting the rest.</summary>
    /// <param name="rows">The changes whose subjects are listed.</param>
    /// <param name="shown">How many subjects to name.</param>
    /// <returns>The list.</returns>
    public static string Changes(IReadOnlyList<OutboundHint> rows, int shown = 5)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var names = rows.Select(row => Subject(row.Kind, row.ItemPath, row.UserName, row.Key)).Distinct(StringComparer.Ordinal).ToList();
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

    /// <summary>Writes one entry, cut to what the Activity page keeps, without ever throwing.</summary>
    private Task WriteAsync(string name, string type, string? overview, LogLevel severity)
        => _activity.LogAndWaitAsync(name, type, ActivityText.Sentence(overview), severity);
}
