using System;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Receiving side of history negotiation. Each entry is handled as one compare and set: read the live
/// state, decide, write, read back. The three steps run under one gate so two callers proposing
/// writes for the same user and item cannot both pass the expectation check and overwrite each other.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerHistoryService
{
    private readonly IUserHistoryStore _store;
    private readonly ILogger<PeerHistoryService> _logger;
    private readonly ApplyGuard? _guard;
    private readonly HistoryAgreementRecorder? _recorder;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerHistoryService"/> class.
    /// </summary>
    /// <param name="store">The history store to read and write through.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="guard">The apply guard, so a write made for a peer raises no hint back to it. Optional for tests.</param>
    /// <param name="recorder">Records what the two servers agreed on in this server's own row for the sender. Optional for tests.</param>
    public PeerHistoryService(IUserHistoryStore store, ILogger<PeerHistoryService> logger, ApplyGuard? guard = null, HistoryAgreementRecorder? recorder = null)
    {
        _store = store;
        _logger = logger;
        _guard = guard;
        _recorder = recorder;
    }

    /// <summary>Handles a whole request, one result per entry in request order.</summary>
    /// <param name="request">The proposed writes.</param>
    /// <returns>The outcomes.</returns>
    public PeerHistoryResponse Negotiate(PeerHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = new PeerHistoryResponse();
        var applied = 0;
        var stale = 0;

        foreach (var entry in request.Items)
        {
            var result = Handle(entry);
            response.Items.Add(result);
            if (result.Outcome is PeerHistoryOutcome.Applied or PeerHistoryOutcome.Unchanged
                && Guid.TryParse(entry.UserId, out var agreedUser) && Guid.TryParse(entry.ItemId, out var agreedItem))
            {
                _recorder?.Record(request.SenderServerId, agreedUser, agreedItem, entry.SenderUserId, entry.SenderItemId, entry.Proposed);
            }

            if (result.Outcome == PeerHistoryOutcome.Applied)
            {
                applied++;
            }
            else if (result.Outcome == PeerHistoryOutcome.Stale)
            {
                stale++;
            }
        }

        _logger.LogInformation(
            "Peer {Sender} negotiated {Count} history entries: {Applied} applied, {Stale} stale",
            string.IsNullOrWhiteSpace(request.SenderServerId) ? "(unknown)" : request.SenderServerId,
            request.Items.Count,
            applied,
            stale);

        return response;
    }

    /// <summary>Handles one entry as an atomic compare and set.</summary>
    /// <param name="entry">The proposed write.</param>
    /// <returns>The outcome, with this server's state after handling.</returns>
    public PeerHistoryResult Handle(PeerHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var result = new PeerHistoryResult { UserId = entry.UserId, ItemId = entry.ItemId };

        if (!Guid.TryParse(entry.UserId, out var userId) || !Guid.TryParse(entry.ItemId, out var itemId))
        {
            result.Outcome = PeerHistoryOutcome.NotFound;
            result.Reason = "user or item id is not a valid id";
            return result;
        }

        if (entry.Proposed is null)
        {
            result.Outcome = PeerHistoryOutcome.Failed;
            result.Reason = "entry has no proposed state";
            return result;
        }

        lock (_gate)
        {
            var current = _store.Read(userId, itemId);
            if (current is null)
            {
                result.Outcome = PeerHistoryOutcome.NotFound;
                result.Reason = "user or item not found on this server";
                return result;
            }

            result.Current = current;
            result.Outcome = PeerHistoryNegotiator.Decide(current, entry.Expected, entry.Proposed);

            if (result.Outcome == PeerHistoryOutcome.Stale)
            {
                result.Reason = "state on this server changed since it was read";
                return result;
            }

            if (result.Outcome != PeerHistoryOutcome.Applied)
            {
                return result;
            }

            bool written;
            using (_guard?.Enter(HintProtocol.GuardKey(HintKind.History, HintProtocol.HistoryKey(userId, itemId))))
            {
                written = _store.Write(userId, itemId, entry.Proposed);
            }

            if (!written)
            {
                result.Outcome = PeerHistoryOutcome.Failed;
                result.Reason = "write to user data failed";
                return result;
            }

            var fresh = _store.Read(userId, itemId);
            if (fresh is null)
            {
                result.Outcome = PeerHistoryOutcome.Failed;
                result.Reason = "user data row not found after write";
                return result;
            }

            result.Current = fresh;
            if (!PeerHistoryNegotiator.StatesMatch(fresh, entry.Proposed))
            {
                result.Outcome = PeerHistoryOutcome.Failed;
                result.Reason = "verification mismatch after write";
            }

            return result;
        }
    }
}
