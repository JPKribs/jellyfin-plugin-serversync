using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Writes the Change queue's events to the Activity page.
/// Changes from peers are summed into one entry for each outcome, such as "Items synced", so a busy sync never floods it.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HintActivityLog : IDisposable
{
    private readonly ActivityLogger _activity;
    private readonly ActivitySummary _summary;
    private readonly IServerConfigurationManager? _serverConfig;
    private readonly ActivityOutcome _synced;
    private readonly ActivityOutcome _notSynced;
    private readonly ActivityOutcome _retrying;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintActivityLog"/> class.
    /// </summary>
    /// <param name="activity">Writes entries to the Activity page.</param>
    /// <param name="serverConfig">Gives the server's display language the entries are written in, or <c>null</c> for US English.</param>
    public HintActivityLog(ActivityLogger activity, IServerConfigurationManager? serverConfig = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
        _serverConfig = serverConfig;
        _summary = new ActivitySummary(activity, language: () => Language);
        _synced = new ActivityOutcome(
            "ServerSync.HintsApplied",
            TranslationKeys.ActivitySyncedNameOne,
            TranslationKeys.ActivitySyncedNameMany,
            (t, language) => BaseTranslations.Format(TranslationKeys.ActivitySynced, language, CountItems(t.Total, language), Counted(t.Sources, language)));
        _notSynced = new ActivityOutcome(
            "ServerSync.HintsDropped",
            TranslationKeys.ActivityNotSyncedNameOne,
            TranslationKeys.ActivityNotSyncedNameMany,
            (t, language) => BaseTranslations.Format(TranslationKeys.ActivityNotSynced, language, CountItems(t.Total, language), Counted(t.Sources, language), Counted(t.Reasons, language)));
        _retrying = new ActivityOutcome(
            "ServerSync.HintsRetrying",
            TranslationKeys.ActivityRetryingNameOne,
            TranslationKeys.ActivityRetryingNameMany,
            (t, language) => BaseTranslations.Format(TranslationKeys.ActivityRetrying, language, CountItems(t.Total, language), Counted(t.Sources, language)),
            LogLevel.Warning);
    }

    /// <summary>Gets the language entries are written in, the server's display language.</summary>
    private string? Language => _serverConfig is null ? null : BaseTranslations.ServerLanguage(_serverConfig);

    /// <summary>Gets a string in the entries' language with its placeholders filled.</summary>
    private string Text(string key, params object?[] args) => BaseTranslations.Format(key, Language, args);

    /// <summary>Counts items, such as "1 item" or "3 items", in a language.</summary>
    private static string CountItems(int count, string? language)
        => BaseTranslations.Format(count == 1 ? TranslationKeys.CountItemOne : TranslationKeys.ItemCount, language, count);

    /// <summary>Counts changes, such as "1 change" or "3 changes", in a language.</summary>
    private static string CountChanges(int count, string? language)
        => BaseTranslations.Format(count == 1 ? TranslationKeys.CountChangeOne : TranslationKeys.CountChangeMany, language, count);

    /// <summary>Describes what a hint concerns, for logs and the dashboard.</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <param name="language">The language, or <c>null</c> for US English, as the log reads it.</param>
    /// <returns>The description.</returns>
    public static string Describe(HintKind kind, string? itemPath, string? userName, string key, string? language = null)
    {
        var file = string.IsNullOrEmpty(itemPath) ? null : Path.GetFileNameWithoutExtension(itemPath);
        return kind switch
        {
            HintKind.History => string.IsNullOrEmpty(userName)
                ? BaseTranslations.Format(TranslationKeys.DescribeHistory, language, file ?? key)
                : BaseTranslations.Format(TranslationKeys.DescribeHistoryOf, language, file ?? key, userName),
            HintKind.Metadata => BaseTranslations.Format(TranslationKeys.DescribeMetadata, language, file ?? key),
            HintKind.People => BaseTranslations.Format(TranslationKeys.DescribePerson, language, userName ?? key),
            HintKind.Content => BaseTranslations.Format(TranslationKeys.DescribeFile, language, file ?? key),
            HintKind.Users => BaseTranslations.Format(TranslationKeys.DescribeUser, language, userName ?? key),
            HintKind.Playlists => BaseTranslations.Format(TranslationKeys.DescribePlaylist, language, userName ?? key),
            _ => key
        };
    }

    /// <summary>Words the subject of a hint as a short title, such as "Metadata for Only A (2021)".</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
    /// <param name="itemPath">The item's path, when the kind concerns an item.</param>
    /// <param name="userName">The username, when the kind concerns a user.</param>
    /// <param name="key">The key, as a fallback.</param>
    /// <param name="language">The language, or <c>null</c> for US English.</param>
    /// <returns>The subject.</returns>
    public static string Subject(HintKind kind, string? itemPath, string? userName, string key, string? language = null)
        => SubjectOf(kind, NameOf(kind, itemPath, userName, key), userName, language);

    /// <summary>Words the subject of a hint around a name already chosen, such as a Jellyfin title.</summary>
    /// <param name="kind">The sync type the hint belongs to.</param>
    /// <param name="name">The item's, person's, or user's name.</param>
    /// <param name="userName">The username, when watch history is concerned.</param>
    /// <param name="language">The language, or <c>null</c> for US English.</param>
    /// <returns>The subject.</returns>
    public static string SubjectOf(HintKind kind, string name, string? userName, string? language = null) => kind switch
    {
        HintKind.History => string.IsNullOrEmpty(userName)
            ? BaseTranslations.Format(TranslationKeys.SubjectHistory, language, name)
            : BaseTranslations.Format(TranslationKeys.SubjectHistoryOf, language, name, userName),
        HintKind.Metadata => BaseTranslations.Format(TranslationKeys.SubjectMetadata, language, name),
        HintKind.People => BaseTranslations.Format(TranslationKeys.SubjectPerson, language, name),
        HintKind.Content => BaseTranslations.Format(TranslationKeys.SubjectFile, language, name),
        HintKind.Users => BaseTranslations.Format(TranslationKeys.SubjectUser, language, name),
        HintKind.Playlists => BaseTranslations.Format(TranslationKeys.SubjectPlaylist, language, name),
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
    /// Records that a hint from a peer was applied here, written together with the others as "Items synced".
    /// </summary>
    /// <param name="hint">The hint that was applied.</param>
    /// <param name="originName">The name the entry gives the origin.</param>
    /// <param name="result">How the apply ended.</param>
    /// <returns>A completed task.</returns>
    public Task AppliedAsync(InboundHint hint, string originName, HintApplyResult result = default)
    {
        ArgumentNullException.ThrowIfNull(hint);
        _summary.Add(_synced, originName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records that a hint from a peer was dropped here, written together with the others as "Items not synced".
    /// </summary>
    /// <param name="hint">The hint that was dropped.</param>
    /// <param name="originName">The name the entry gives the origin.</param>
    /// <param name="code">Why it was dropped.</param>
    /// <returns>A completed task.</returns>
    public Task DroppedAsync(InboundHint hint, string originName, HintCode code)
    {
        ArgumentNullException.ThrowIfNull(hint);
        _summary.Add(_notSynced, originName, ReasonOf(code, Language));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records that a hint from a peer keeps failing here, on its second and sixth try, as "Items not applied yet".
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
            _summary.Add(_retrying, originName);
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
        var at = until.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        var overview = waiting.Count == 0
            ? Text(TranslationKeys.ActivityPausedOverview, peerName, at, reason)
            : Text(TranslationKeys.ActivityPausedWaiting, peerName, at, reason, CountChanges(waiting.Count, Language));
        return WriteAsync(
            Text(TranslationKeys.ActivityPausedName),
            "ServerSync.PeerPaused",
            overview,
            LogLevel.Warning);
    }

    /// <summary>Records that a peer rejected a whole request, once for the batch.</summary>
    /// <param name="peerName">The peer's display name.</param>
    /// <param name="rows">The hints the request carried.</param>
    /// <returns>A task that ends when the entry is written.</returns>
    public Task RejectedBatchAsync(string peerName, IReadOnlyList<OutboundHint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(
            Text(TranslationKeys.ActivityRejectedName),
            "ServerSync.HintRejected",
            Text(TranslationKeys.ActivityRejectedOverview, peerName, CountChanges(rows.Count, Language)),
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
            Text(TranslationKeys.ActivityResentName),
            "ServerSync.HintResent",
            Text(TranslationKeys.ActivityResent, CountChanges(rows.Count, Language), peerName),
            LogLevel.Warning);
    }

    /// <inheritdoc />
    public void Dispose() => _summary.Dispose();

    /// <summary>Writes whatever summaries are waiting.</summary>
    public void Flush() => _summary.Flush();

    /// <summary>Words a short reason a change was not synced, for the summary.</summary>
    /// <param name="code">The code it was dropped with.</param>
    /// <param name="language">The language, or <c>null</c> for US English.</param>
    /// <returns>The reason.</returns>
    public static string ReasonOf(HintCode code, string? language = null) => BaseTranslations.Get(
        code switch
        {
            HintCode.Filtered => TranslationKeys.ReasonFiltered,
            HintCode.NotMapped => TranslationKeys.ReasonNotMapped,
            HintCode.Ignored => TranslationKeys.ReasonIgnored,
            HintCode.ModuleOff => TranslationKeys.ReasonModuleOff,
            HintCode.Priority => TranslationKeys.ReasonPriority,
            HintCode.SourceGone => TranslationKeys.ReasonSourceGone,
            HintCode.Unreadable => TranslationKeys.ReasonUnreadable,
            HintCode.NoLocalItem => TranslationKeys.ReasonNoLocalItem,
            HintCode.GaveUp => TranslationKeys.ReasonGaveUp,
            HintCode.Unsupported => TranslationKeys.ReasonUnsupported,
            HintCode.Invalid => TranslationKeys.ReasonInvalid,
            _ => TranslationKeys.ReasonCouldNotApply
        },
        language);

    /// <summary>Lists names with their counts, the largest first, such as "source-a (9) and source-b". A count of 1 is left out, and so is the count of a single name.</summary>
    /// <param name="counts">The counts by name. A count of zero or less is left out, and equal counts sort by name.</param>
    /// <param name="language">The language, or <c>null</c> for US English.</param>
    /// <returns>The list, or an empty string when there is nothing to list.</returns>
    public static string Counted(IReadOnlyDictionary<string, int> counts, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var listed = counts
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        var names = listed
            .Select(p => listed.Count == 1 || p.Value == 1 ? p.Key : string.Create(CultureInfo.InvariantCulture, $"{p.Key} ({p.Value})"))
            .ToList();
        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => BaseTranslations.Format(TranslationKeys.ListTwo, language, names[0], names[1]),
            _ => BaseTranslations.Format(TranslationKeys.ListLast, language, string.Join(", ", names.GetRange(0, names.Count - 1)), names[^1])
        };
    }

    /// <summary>Lists the subjects of some changes in a sentence, naming at most a few and counting the rest.</summary>
    /// <param name="rows">The changes whose subjects are listed.</param>
    /// <param name="shown">How many subjects to name.</param>
    /// <param name="language">The language, or <c>null</c> for US English.</param>
    /// <returns>The list.</returns>
    public static string Changes(IReadOnlyList<OutboundHint> rows, int shown = 5, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var names = rows.Select(row => Subject(row.Kind, row.ItemPath, row.UserName, row.Key, language)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return BaseTranslations.Get(TranslationKeys.ChangesWord, language);
        }

        var listed = names.Count > shown ? names.GetRange(0, shown) : names;
        if (names.Count > shown)
        {
            return BaseTranslations.Format(TranslationKeys.ListAndMore, language, string.Join(", ", listed), names.Count - shown);
        }

        return listed.Count switch
        {
            1 => listed[0],
            2 => BaseTranslations.Format(TranslationKeys.ListTwo, language, listed[0], listed[1]),
            _ => BaseTranslations.Format(TranslationKeys.ListLast, language, string.Join(", ", listed.GetRange(0, listed.Count - 1)), listed[^1])
        };
    }

    /// <summary>Writes one entry, cut to what the Activity page keeps, without ever throwing.</summary>
    private Task WriteAsync(string name, string type, string? overview, LogLevel severity)
        => _activity.LogAndWaitAsync(name, type, ActivityText.Sentence(overview), severity);
}
