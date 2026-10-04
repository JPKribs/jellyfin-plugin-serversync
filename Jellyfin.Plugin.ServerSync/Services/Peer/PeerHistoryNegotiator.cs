using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// The pure decision logic behind the peer history endpoint. Kept free of Jellyfin services so the
/// compare and set rule can be tested on its own.
/// </summary>
public static class PeerHistoryNegotiator
{
    /// <summary>The feature name a peer advertises when it accepts negotiated history writes.</summary>
    public const string HistoryFeature = "history-negotiate";

    /// <summary>Hard cap on entries per request, so an elevated caller cannot queue unbounded work.</summary>
    public const int MaxEntriesPerRequest = 500;

    /// <summary>
    /// Decides what to do with one entry given the receiver's live state. The receiver writes only when
    /// its live state matches what the sender expected, and skips the write when it already holds the
    /// proposal.
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

        // A sender with no reading of this server cannot know whether it is overwriting anything, so
        // it is told the live state and asked again. A reading with no fields at all is no reading.
        if (expected is null || IsEmpty(expected) || !StatesMatch(current, expected))
        {
            return PeerHistoryOutcome.Stale;
        }

        return PeerHistoryOutcome.Applied;
    }

    /// <summary>Whether a state carries no field at all.</summary>
    /// <param name="state">The state.</param>
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
    /// Sender side: turns the peer's answer for one row into the next step. A stale answer that
    /// carries the peer's live state is folded into the row (source fields replaced, merge rerun, source
    /// bundle refreshed) so the caller can offer the row once more. Anything else either proceeds to
    /// the local write or fails with the peer's reason.
    /// </summary>
    /// <param name="record">The history row that was offered.</param>
    /// <param name="result">The peer's answer.</param>
    /// <param name="allowRetry">Whether a stale answer may lead to another offer.</param>
    /// <returns>The step to take and, for a failure, the reason.</returns>
    public static NegotiationStep ResolveOutcome(HistorySyncItem record, PeerHistoryResult result, bool allowRetry)
        => ResolveOutcome(record, result, allowRetry, remerge: null);

    /// <summary>
    /// Like <see cref="ResolveOutcome(HistorySyncItem, PeerHistoryResult, bool)"/>, with a step that runs
    /// after a stale answer is merged again, so a caller that decided the merge some other way, such as
    /// by edit versions, keeps that decision on the retry.
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
    /// Compares two states field by field. A null on either side of a field counts as a match, since
    /// null means "no opinion" rather than a value, and dates compare at second resolution because
    /// Jellyfin does not keep sub second precision.
    /// </summary>
    /// <param name="a">First state.</param>
    /// <param name="b">Second state.</param>
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

    // A state that carries a played flag has a real reading of the date, so a null there means "no
    // date", which is how Jellyfin stores an item marked unplayed. Only a state with no played flag
    // at all is treated as having no opinion about the date.
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
    /// <param name="data">The user data row.</param>
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
    /// <param name="item">The history row.</param>
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
    /// <param name="item">The history row.</param>
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

    /// <summary>Writes a peer's live state back onto the source side of a history row.</summary>
    /// <param name="item">The history row to update.</param>
    /// <param name="state">The peer's live state.</param>
    public static void ApplySourceState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        item.SourceIsPlayed = state.Played;
        item.SourcePlayCount = state.PlayCount;
        item.SourcePlaybackPositionTicks = state.PlaybackPositionTicks;
        item.SourceLastPlayedDate = state.LastPlayedDate;
        item.SourceIsFavorite = state.IsFavorite;
    }

    /// <summary>Builds the state this server holds, as the history row records it.</summary>
    /// <param name="item">The history row.</param>
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
    /// <param name="item">The history row.</param>
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
    /// <param name="state">The state.</param>
    public static void ApplyLocalState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        item.LocalIsPlayed = state.Played;
        item.LocalPlayCount = state.PlayCount;
        item.LocalPlaybackPositionTicks = state.PlaybackPositionTicks;
        item.LocalLastPlayedDate = state.LastPlayedDate;
        item.LocalIsFavorite = state.IsFavorite;
    }

    /// <summary>Writes a state as the merged result of a history row.</summary>
    /// <param name="item">The history row to update.</param>
    /// <param name="state">The state.</param>
    public static void ApplyMergedState(HistorySyncItem item, PeerHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        item.MergedIsPlayed = state.Played;
        item.MergedPlayCount = state.PlayCount;
        item.MergedPlaybackPositionTicks = state.PlaybackPositionTicks;
        item.MergedLastPlayedDate = state.LastPlayedDate;
        item.MergedIsFavorite = state.IsFavorite;
    }

    private static bool Same<T>(T? a, T? b)
        where T : struct
        => !a.HasValue || !b.HasValue || a.Value.Equals(b.Value);
}
