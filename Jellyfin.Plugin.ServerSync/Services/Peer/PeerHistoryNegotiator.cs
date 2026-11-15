using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Holds the compare and set rule behind the peer history route, free of Jellyfin services.
/// </summary>
public static class PeerHistoryNegotiator
{
    /// <summary>The most entries one request may carry, so a caller cannot queue unbounded work.</summary>
    public const int MaxEntriesPerRequest = 500;

    /// <summary>
    /// Decides what to do with one entry, writing only when the receiver's live state matches what the sender expected.
    /// </summary>
    /// <param name="current">The receiver's live state.</param>
    /// <param name="expected">What the sender believed the receiver held, or null when it had no reading.</param>
    /// <param name="proposed">The state the sender wants written.</param>
    /// <returns>Applied when the write should go ahead, Unchanged when nothing differs, Stale when the live state moved.</returns>
    public static PeerHistoryOutcome Decide(PeerHistoryState current, PeerHistoryState? expected, PeerHistoryState proposed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);

        if (StatesMatch(current, proposed))
        {
            return PeerHistoryOutcome.Unchanged;
        }

        // A sender with no reading cannot know what it overwrites, so it asks again.
        if (expected is null || IsEmpty(expected) || !StatesMatch(current, expected))
        {
            return PeerHistoryOutcome.Stale;
        }

        return PeerHistoryOutcome.Applied;
    }

    /// <summary>Whether a state carries no field at all.</summary>
    /// <param name="state">The state to check.</param>
    /// <returns><c>true</c> when every field is null.</returns>
    public static bool IsEmpty(PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.Played.HasValue
            && !state.PlayCount.HasValue
            && !state.PlaybackPositionTicks.HasValue
            && !state.LastPlayedDate.HasValue
            && !state.IsFavorite.HasValue;
    }

    /// <summary>
    /// Turns the peer's answer for one row into the sender's next step.
    /// A stale answer that carries the peer's live state is merged into the row so the caller can offer it once more.
    /// </summary>
    /// <param name="record">The history row that was offered.</param>
    /// <param name="result">The peer's answer.</param>
    /// <param name="allowRetry">Whether a stale answer may lead to another offer.</param>
    /// <returns>The step to take and, for a failure, the reason.</returns>
    public static NegotiationStep ResolveOutcome(HistorySyncItem record, PeerHistoryResult result, bool allowRetry)
        => ResolveOutcome(record, result, allowRetry, remerge: null);

    /// <summary>
    /// Turns the peer's answer for one row into the sender's next step, running a step after a stale answer is merged again.
    /// A caller that decided the merge another way, such as by stamps, keeps that decision on the retry.
    /// </summary>
    /// <param name="record">The row being negotiated.</param>
    /// <param name="result">The peer's answer.</param>
    /// <param name="allowRetry">Whether a stale answer may be retried once.</param>
    /// <param name="remerge">Runs after the row is merged again against the peer's live state, or null.</param>
    /// <returns>What to do next.</returns>
    public static NegotiationStep ResolveOutcome(HistorySyncItem record, PeerHistoryResult result, bool allowRetry, Action<HistorySyncItem>? remerge)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case PeerHistoryOutcome.Applied:
            case PeerHistoryOutcome.Unchanged:
                return new NegotiationStep(NegotiationAction.Proceed, null);

            case PeerHistoryOutcome.Stale when allowRetry && result.Current is not null:
                record.NegotiateWithSource = true;
                ApplySourceState(record, result.Current);
                HistorySyncMergeService.MergeHistoryData(record);
                remerge?.Invoke(record);
                record.UpdateSourceStateBundle();
                return new NegotiationStep(NegotiationAction.Retry, null);

            case PeerHistoryOutcome.Stale:
                return new NegotiationStep(NegotiationAction.Fail, "source state kept changing during negotiation, will retry on the next run");

            case PeerHistoryOutcome.NotFound:
                return new NegotiationStep(NegotiationAction.Fail, "source rejected the write: " + (result.Reason ?? "user or item not found"));

            default:
                return new NegotiationStep(NegotiationAction.Fail, "source failed the write: " + (result.Reason ?? "unknown"));
        }
    }

    /// <summary>
    /// Compares two states field by field, where a null on either side matches because it means no opinion.
    /// Dates compare to the second because Jellyfin keeps no finer precision.
    /// </summary>
    /// <param name="a">One state to compare.</param>
    /// <param name="b">The state to compare it with.</param>
    /// <returns><c>true</c> when no field holds two different values.</returns>
    public static bool StatesMatch(PeerHistoryState a, PeerHistoryState b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return Same(a.Played, b.Played)
            && Same(a.PlayCount, b.PlayCount)
            && Same(a.PlaybackPositionTicks, b.PlaybackPositionTicks)
            && Same(a.IsFavorite, b.IsFavorite)
            && DatesMatch(a, b);
    }

    /// <summary>
    /// Compares the dates of two states. A null date beside a played flag means no date, and only a state with no played flag has no opinion.
    /// </summary>
    private static bool DatesMatch(PeerHistoryState a, PeerHistoryState b)
    {
        if (!a.Played.HasValue || !b.Played.HasValue)
        {
            return !a.LastPlayedDate.HasValue || !b.LastPlayedDate.HasValue
                || HistorySyncMergeService.SameInstantToSecond(a.LastPlayedDate, b.LastPlayedDate);
        }

        return HistorySyncMergeService.SameInstantToSecond(a.LastPlayedDate, b.LastPlayedDate);
    }

    /// <summary>Reads a Jellyfin user data row into the wire shape.</summary>
    /// <param name="data">The user data row to read.</param>
    /// <returns>The equivalent peer state.</returns>
    public static PeerHistoryState FromUserData(UserItemData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PeerHistoryState
        {
            Played = data.Played,
            PlayCount = data.PlayCount,
            PlaybackPositionTicks = data.PlaybackPositionTicks,
            LastPlayedDate = data.LastPlayedDate,
            IsFavorite = data.IsFavorite
        };
    }

    /// <summary>Builds the state the local server believes the source currently holds.</summary>
    /// <param name="item">The history row to read.</param>
    /// <returns>The source side of the row as a peer state.</returns>
    public static PeerHistoryState SourceStateOf(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PeerHistoryState
        {
            Played = item.SourceIsPlayed,
            PlayCount = item.SourcePlayCount,
            PlaybackPositionTicks = item.SourcePlaybackPositionTicks,
            LastPlayedDate = item.SourceLastPlayedDate,
            IsFavorite = item.SourceIsFavorite
        };
    }

    /// <summary>Builds the merged state the local server wants both servers to hold.</summary>
    /// <param name="item">The history row to read.</param>
    /// <returns>The merged side of the row as a peer state.</returns>
    public static PeerHistoryState MergedStateOf(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PeerHistoryState
        {
            Played = item.MergedIsPlayed,
            PlayCount = item.MergedPlayCount,
            PlaybackPositionTicks = item.MergedPlaybackPositionTicks,
            LastPlayedDate = item.MergedLastPlayedDate,
            IsFavorite = item.MergedIsFavorite
        };
    }

    /// <summary>Writes a peer's live state onto the source side of a history row, keeping any part the state does not carry.</summary>
    /// <param name="item">The history row to update.</param>
    /// <param name="state">The peer's live state.</param>
    public static void ApplySourceState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);

        item.SourceIsPlayed = state.Played ?? item.SourceIsPlayed;
        item.SourcePlayCount = state.PlayCount ?? item.SourcePlayCount;
        item.SourcePlaybackPositionTicks = state.PlaybackPositionTicks ?? item.SourcePlaybackPositionTicks;
        item.SourceLastPlayedDate = DateOf(state, item.SourceLastPlayedDate);
        item.SourceIsFavorite = state.IsFavorite ?? item.SourceIsFavorite;
    }

    /// <summary>Builds the state this server holds, as the history row records it.</summary>
    /// <param name="item">The history row to read.</param>
    /// <returns>The local state.</returns>
    public static PeerHistoryState LocalStateOf(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PeerHistoryState
        {
            Played = item.LocalIsPlayed,
            PlayCount = item.LocalPlayCount,
            PlaybackPositionTicks = item.LocalPlaybackPositionTicks,
            LastPlayedDate = item.LocalLastPlayedDate,
            IsFavorite = item.LocalIsFavorite
        };
    }

    /// <summary>Builds the state the two servers last agreed on.</summary>
    /// <param name="item">The history row to read.</param>
    /// <returns>The negotiated state.</returns>
    public static PeerHistoryState NegotiatedStateOf(HistorySyncItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new PeerHistoryState
        {
            Played = item.NegotiatedIsPlayed,
            PlayCount = item.NegotiatedPlayCount,
            PlaybackPositionTicks = item.NegotiatedPlaybackPositionTicks,
            LastPlayedDate = item.NegotiatedLastPlayedDate,
            IsFavorite = item.NegotiatedIsFavorite
        };
    }

    /// <summary>Writes a state onto the local side of a history row.</summary>
    /// <param name="item">The history row to update.</param>
    /// <param name="state">The state this server holds.</param>
    public static void ApplyLocalState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        item.LocalIsPlayed = state.Played ?? item.LocalIsPlayed;
        item.LocalPlayCount = state.PlayCount ?? item.LocalPlayCount;
        item.LocalPlaybackPositionTicks = state.PlaybackPositionTicks ?? item.LocalPlaybackPositionTicks;
        item.LocalLastPlayedDate = DateOf(state, item.LocalLastPlayedDate);
        item.LocalIsFavorite = state.IsFavorite ?? item.LocalIsFavorite;
    }

    /// <summary>Writes a state as the merged result of a history row.</summary>
    /// <param name="item">The history row to update.</param>
    /// <param name="state">The state both servers should hold.</param>
    public static void ApplyMergedState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        item.MergedIsPlayed = state.Played;
        item.MergedPlayCount = state.PlayCount;
        item.MergedPlaybackPositionTicks = state.PlaybackPositionTicks;
        item.MergedLastPlayedDate = state.LastPlayedDate;
        item.MergedIsFavorite = state.IsFavorite;
        HistorySyncMergeService.KeepEnabledFields(item);
    }

    /// <summary>Copies a state without the parts that are turned off in the History settings.</summary>
    /// <param name="state">The state to copy.</param>
    /// <returns>The state this server may write or propose.</returns>
    public static PeerHistoryState EnabledPartOf(PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fields = HistorySyncMergeService.CurrentFields();
        return new PeerHistoryState
        {
            Played = fields.HasFlag(HistoryFields.Played) ? state.Played : null,
            PlayCount = fields.HasFlag(HistoryFields.PlayCount) ? state.PlayCount : null,
            PlaybackPositionTicks = fields.HasFlag(HistoryFields.Position) ? state.PlaybackPositionTicks : null,
            LastPlayedDate = fields.HasFlag(HistoryFields.LastPlayed) ? state.LastPlayedDate : null,
            IsFavorite = fields.HasFlag(HistoryFields.Favorite) ? state.IsFavorite : null
        };
    }

    /// <summary>
    /// Picks the date a state gives a row. A state without one clears the date only when it carries a played mark or is unplayed.
    /// </summary>
    private static DateTime? DateOf(PeerHistoryState state, DateTime? kept)
    {
        if (state.LastPlayedDate.HasValue)
        {
            return state.LastPlayedDate;
        }

        var dated = HistorySyncMergeService.CurrentFields().HasFlag(HistoryFields.LastPlayed) && state.Played.HasValue;
        return dated || state.Played == false ? null : kept;
    }

    /// <summary>Whether two optional values agree, where a missing value agrees with anything.</summary>
    private static bool Same<T>(T? a, T? b)
        where T : struct
        => !a.HasValue || !b.HasValue || a.Value.Equals(b.Value);
}
