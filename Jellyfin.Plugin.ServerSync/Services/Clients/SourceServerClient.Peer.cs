using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Calls to a peer that runs Server Sync: capabilities, pairing, hints, history negotiation, and versions.
/// </summary>
public partial class SourceServerClient
{
    /// <summary>
    /// Reads peer JSON in either casing and writes enums as strings, as Jellyfin serializes them.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions PeerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Asks the source whether it runs Server Sync and what it can negotiate.
    /// Any failure but a 404 is raised with its status, so a caller can tell a refused key from a peer that is down.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's capabilities, or null when the source answers 404 because Server Sync is not installed there.</returns>
    public async Task<PeerCapabilities?> GetPeerCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<PeerCapabilities>(HttpMethod.Get, "/Sync/Peer/Capabilities", null, cancellationToken).ConfigureAwait(false);
        if (status == 404)
        {
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the capabilities request: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Asks the peer whether its Server Sync lets this server's key use the sync routes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A result code, such as Allowed or NotInstalled, and a message that says it in US English.</returns>
    public Task<(string Code, string Message)> CheckSyncAccessAsync(CancellationToken cancellationToken = default)
        => CheckSyncAccessAsync(null, cancellationToken);

    /// <summary>
    /// Asks the peer whether its Server Sync lets this server's key use the sync routes, with the message in a language.
    /// </summary>
    /// <param name="language">The language of the message, or <c>null</c> for US English.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A result code, such as Allowed or NotInstalled, and a message that says it.</returns>
    public async Task<(string Code, string Message)> CheckSyncAccessAsync(string? language, CancellationToken cancellationToken)
    {
        int status;
        string? body;
        try
        {
            (status, _, body) = await PeerRequestAsync<PeerCapabilities>(HttpMethod.Get, "/Sync/Peer/Capabilities", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        }
        // Any failure to reach the peer is a result to show, not an error.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ("Unknown", BaseTranslations.Format(TranslationKeys.AccessUnknown, language, ex.Message));
        }

        if (status is >= 200 and < 300)
        {
            return ("Allowed", BaseTranslations.Get(TranslationKeys.AccessKeyAllowed, language));
        }

        if (status == 404)
        {
            return ("NotInstalled", BaseTranslations.Get(TranslationKeys.AccessNotInstalled, language));
        }

        if (status == 426)
        {
            return ("VersionMismatch", body ?? BaseTranslations.Get(TranslationKeys.AccessVersionMismatch, language));
        }

        if (status == 403 && !string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("Code", out var code)
                    && root.TryGetProperty("Message", out var message))
                {
                    return (code.GetString() ?? "Refused", message.GetString() ?? BaseTranslations.Get(TranslationKeys.ColumnRefused, language));
                }
            }
            // A body that is not JSON falls through to the plain refusal.
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return ("Refused", BaseTranslations.Format(TranslationKeys.AccessRefusedStatus, language, status));
    }

    /// <summary>
    /// Reads the playlists the peer's Server Sync offers this server.
    /// </summary>
    /// <param name="callerServerId">This server's id.</param>
    /// <param name="onlyId">The one playlist to read, or null for all.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The playlists, or null when the peer's Server Sync does not list them.</returns>
    public async Task<PeerPlaylistsResponse?> GetPeerPlaylistsAsync(string callerServerId, Guid? onlyId = null, CancellationToken cancellationToken = default)
    {
        var path = "/Sync/Peer/Playlists?serverId=" + Uri.EscapeDataString(callerServerId ?? string.Empty)
            + (onlyId is null ? string.Empty : "&id=" + onlyId.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var (status, parsed, body) = await PeerRequestAsync<PeerPlaylistsResponse>(HttpMethod.Get, path, null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status == 426)
        {
            throw new HttpRequestException($"The peer runs another Server Sync version, so playlists wait until both match: {body}", null, (System.Net.HttpStatusCode)status);
        }

        if (status is 401 or 403 or 404)
        {
            _logger.LogDebug("The peer's Server Sync did not list its playlists ({Status}): {Body}", status, FormatUtilities.SanitizeForLog(body));
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the playlists request: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerPlaylistsResponse();
    }

    /// <summary>
    /// Lists the playlists a source user can see through Jellyfin's own API.
    /// </summary>
    /// <param name="userId">The source user whose playlists are listed.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The playlists, sorted by name.</returns>
    public async Task<List<BaseItemDto>> GetVisiblePlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var client = GetApiClient();
        var result = await client.Items.GetAsync(
            config =>
            {
                config.QueryParameters.UserId = userId;
                config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Playlist };
                config.QueryParameters.Recursive = true;
                config.QueryParameters.Fields = new[] { ItemFields.ProviderIds };
                config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Items?.ToList() ?? throw new InvalidOperationException("The source returned no playlist list");
    }

    /// <summary>
    /// Lists the entries of a playlist in order, keeping repeats.
    /// </summary>
    /// <param name="playlistId">The source playlist.</param>
    /// <param name="userId">The source user the playlist is read as.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The playlist's entries.</returns>
    public Task<List<BaseItemDto>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken cancellationToken = default)
        => GetPlaylistItemsAsync(playlistId, userId, cancellationToken, distinct: false);

    /// <summary>
    /// Sends merged history states to the source's Server Sync plugin. The source writes each one only
    /// if its live state still matches what this server expected, and reports the outcome per entry.
    /// </summary>
    /// <param name="peerRequest">The entries to negotiate.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The source's per entry results.</returns>
    public async Task<PeerHistoryResponse> NegotiateHistoryAsync(PeerHistoryRequest peerRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerRequest);
        var (status, parsed, body) = await PeerRequestAsync<PeerHistoryResponse>(HttpMethod.Post, "/Sync/Peer/History", peerRequest, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new Peer.PeerRefusedException((System.Net.HttpStatusCode)status, $"Source server refused the history negotiation with {status}: {body}");
        }

        return parsed ?? new PeerHistoryResponse();
    }

    /// <summary>
    /// Offers a batch of hints to the peer's Server Sync. Never throws on an HTTP answer: the status
    /// code is returned so the caller can decide between done, paused, and retry.
    /// </summary>
    /// <param name="queueRequest">The hints to offer.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status code, the parsed answer on 200, and the body text otherwise.</returns>
    public async Task<(int StatusCode, Models.Queue.QueueResponse? Response, string? Body)> SendHintsAsync(Models.Queue.QueueRequest queueRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queueRequest);
        var (status, parsed, body) = await PeerRequestAsync<Models.Queue.QueueResponse>(HttpMethod.Post, "/Sync/Peer/Queue", queueRequest, cancellationToken).ConfigureAwait(false);
        return status == 200 ? (status, parsed ?? new Models.Queue.QueueResponse(), null) : (status, null, body);
    }

    /// <summary>
    /// Asks the peer whether it lists this server, and accepts the secret the peer hands over only while the call is open.
    /// </summary>
    /// <param name="thisServerId">This server's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The listing, or null when the peer has no Server Sync that can answer.</returns>
    public async Task<Models.Peer.PeerLinkResponse?> GetPeerLinkAsync(string thisServerId, CancellationToken cancellationToken = default)
    {
        using var expecting = Pairing?.Expect();
        var (_, parsed, _) = await PeerRequestAsync<Models.Peer.PeerLinkResponse>(HttpMethod.Get, "/Sync/Peer/Link?serverId=" + Uri.EscapeDataString(thisServerId ?? string.Empty), null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>
    /// Hands the peer the secret it must present on requests that name it as the sender to this server.
    /// </summary>
    /// <param name="pairRequest">This server's id and the secret.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status the peer answered and its body when it refused.</returns>
    public async Task<(int StatusCode, string? Body)> PairAsync(Models.Peer.PairRequest pairRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairRequest);
        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Pair", pairRequest, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        return (status, body);
    }

    /// <summary>
    /// Reads one person by name with the fields the people module compares.
    /// </summary>
    /// <param name="name">The person's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The person, or null when the peer has none by that name.</returns>
    public async Task<BaseItemDto?> GetPersonByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var client = GetApiClient();
            return await client.Persons[name].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the peer which versions it holds for a batch of its own keys.
    /// </summary>
    /// <param name="versionsRequest">The kind and the keys to ask about.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The versions by key, or null when the peer did not answer with versions.</returns>
    public async Task<Dictionary<string, Models.Queue.ObjectVersion>?> GetPeerVersionsAsync(Models.Queue.VersionsRequest versionsRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionsRequest);
        var (status, parsed, _) = await PeerRequestAsync<Models.Queue.VersionsResponse>(HttpMethod.Post, "/Sync/Peer/Versions", versionsRequest, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            return null;
        }

        var map = new Dictionary<string, Models.Queue.ObjectVersion>(StringComparer.Ordinal);
        foreach (var version in parsed?.Items ?? new List<Models.Queue.ObjectVersion>())
        {
            map[version.Key] = version;
        }

        return map;
    }

    /// <summary>
    /// Tells the origin of some hints that they are done.
    /// </summary>
    /// <param name="completeRequest">The finished hints.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><c>true</c> when the origin accepted the report.</returns>
    public async Task<bool> CompleteHintsAsync(Models.Queue.CompleteRequest completeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completeRequest);
        var (status, _, _) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Complete", completeRequest, cancellationToken).ConfigureAwait(false);
        return status is >= 200 and < 300;
    }

    /// <summary>
    /// Reads which hints the peer wants this server to send it.
    /// </summary>
    /// <param name="thisServerId">This server's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's interest, or null when it gave none.</returns>
    public async Task<PeerInterest?> GetPeerInterestAsync(string thisServerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thisServerId);
        var (status, parsed, body) = await PeerRequestAsync<PeerInterest>(HttpMethod.Get, "/Sync/Peer/Interest?serverId=" + Uri.EscapeDataString(thisServerId), null, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the interest request: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Asks the peer to find an item by its ids or path.
    /// </summary>
    /// <param name="lookupRequest">What to find.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's answer, or null when it gave none.</returns>
    public async Task<PeerLookupResponse?> LookupPeerItemAsync(PeerLookupRequest lookupRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookupRequest);
        var (status, parsed, body) = await PeerRequestAsync<PeerLookupResponse>(HttpMethod.Post, "/Sync/Peer/Lookup", lookupRequest, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the lookup: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Reads the media segments a peer holds for a batch of its items, throwing when it gives no answer, such as a peer on a release without the route.
    /// </summary>
    /// <param name="itemIds">The peer's ids of the items, at most <see cref="PeerSegmentsRequest.MaxItems"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's answer, one entry for each item.</returns>
    public async Task<PeerSegmentsResponse> GetPeerSegmentsAsync(IReadOnlyList<string> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var request = new PeerSegmentsRequest { ItemIds = itemIds.ToList() };
        var (status, parsed, body) = await PeerRequestAsync<PeerSegmentsResponse>(HttpMethod.Post, "/Sync/Peer/Segments", request, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is < 200 or >= 300 || parsed is null)
        {
            throw new HttpRequestException($"The peer answered {status} to the media segment read: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Sends an authorized GET to an address on this server entry's server.
    /// </summary>
    /// <param name="url">The address, which must start with the server URL.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> GetAuthorizedAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!url.StartsWith(_serverUrl + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The address is not on this server", nameof(url));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthorizationHeader(request);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the base address this client calls.
    /// </summary>
    public string ServerUrl => _serverUrl;

    /// <summary>
    /// Reads the hints this server sent the peer that it still holds, and those it finished.
    /// </summary>
    /// <param name="fromServerId">This server's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The status, or null when it could not be read.</returns>
    public async Task<Models.Queue.QueueStatusResponse?> GetPeerQueueStatusAsync(string fromServerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromServerId);
        var (_, parsed, _) = await PeerRequestAsync<Models.Queue.QueueStatusResponse>(HttpMethod.Get, "/Sync/Peer/Status?serverId=" + Uri.EscapeDataString(fromServerId), null, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>
    /// Opens a stream of one of the peer's files for a link here, which the caller owns and disposes.
    /// A peer that does not hold this server's pairing is paired once and asked again.
    /// </summary>
    /// <param name="sourceItemId">The peer's id of the item.</param>
    /// <param name="callerServerId">This server's Jellyfin id.</param>
    /// <param name="grant">Who the stream is for.</param>
    /// <param name="sourceUserId">The peer's user this server maps the user to, or null.</param>
    /// <param name="range">The Range header to pass on to the peer, or null.</param>
    /// <param name="head">Whether to ask for the headers only.</param>
    /// <param name="fill">Whether this downloads the file into this server's link cache, rather than playing it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's answer.</returns>
    public async Task<HttpResponseMessage> OpenPeerStreamAsync(string sourceItemId, string callerServerId, Links.LinkGrant grant, string? sourceUserId, string? range, bool head, bool fill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceItemId);
        ArgumentNullException.ThrowIfNull(grant);
        var path = "/Sync/Peer/Stream/" + Uri.EscapeDataString(sourceItemId)
            + "?serverId=" + Uri.EscapeDataString(callerServerId ?? string.Empty)
            + (grant.UserId is { } user ? "&userId=" + user.ToString("N", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
            + (string.IsNullOrEmpty(sourceUserId) ? string.Empty : "&sourceUserId=" + Uri.EscapeDataString(sourceUserId))
            + (grant.Probe ? "&probe=true" : string.Empty)
            + (fill ? "&fill=true" : string.Empty);
        var response = await SendPeerStreamAsync(path, range, head, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode != Services.Queue.HintProtocol.UnpairedStatus || Pairing is null)
        {
            return response;
        }

        response.Dispose();
        _logger.LogInformation("The peer at {Url} does not hold a pairing for this server, pairing now", FormatUtilities.SanitizeForLog(_serverUrl));
        await GetPeerLinkAsync(Pairing.ThisServerId, cancellationToken).ConfigureAwait(false);
        return await SendPeerStreamAsync(path, range, head, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the peer, before playback starts, whether a user here may stream one of its files through a link.
    /// </summary>
    /// <param name="request">The item and user.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's answer, which carries its reason when it refuses.</returns>
    public async Task<LinkStreamCheckResponse> CheckLinkStreamAsync(LinkStreamCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (status, parsed, body) = await PeerRequestAsync<LinkStreamCheckResponse>(HttpMethod.Post, "/Sync/Peer/Stream/Check", request, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300 || parsed is null)
        {
            return new LinkStreamCheckResponse { Allowed = false, Reason = $"the source answered {status}: {body}" };
        }

        return parsed;
    }

    /// <summary>
    /// Reports playback of a link to the peer that holds the file, and throws when the peer refuses.
    /// </summary>
    /// <param name="report">The playback state to report.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that ends when the peer has answered.</returns>
    public async Task ReportLinkPlaybackAsync(PeerPlaybackReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Playback", report, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to a playback report: {body}", null, (System.Net.HttpStatusCode)status);
        }
    }

    /// <summary>
    /// Passes a remote control command to the peer for a viewer of a link there, and throws when the peer refuses.
    /// </summary>
    /// <param name="request">The command and the viewer it is for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that ends when the peer has answered.</returns>
    public async Task SendLinkControlAsync(PeerControlRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Control", request, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to a remote control command: {body}", null, (System.Net.HttpStatusCode)status);
        }
    }

    /// <summary>
    /// Asks the peer about files this server holds links to.
    /// Throws with the peer's reason when it refuses, such as when Allow linking is off for this server there.
    /// </summary>
    /// <param name="request">The items to describe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One answer per item, or null when the peer's Server Sync has no such route.</returns>
    public async Task<PeerLinkDescribeResponse?> DescribeLinksAsync(PeerLinkDescribeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (status, parsed, body) = await PeerRequestAsync<PeerLinkDescribeResponse>(HttpMethod.Post, "/Sync/Peer/Links/Describe", request, cancellationToken).ConfigureAwait(false);
        if (status is 404 or 405)
        {
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body) ? $"the source answered {status}" : body, null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerLinkDescribeResponse();
    }

    /// <summary>
    /// Opens a chapter image or a trickplay tile the peer made for a file this server links to, which the caller disposes.
    /// </summary>
    /// <param name="sourceItemId">The peer's id of the item.</param>
    /// <param name="kind">Chapter or Trickplay.</param>
    /// <param name="index">The chapter's index, or the tile's.</param>
    /// <param name="width">For a trickplay tile, the width of its set.</param>
    /// <param name="callerServerId">This server's Jellyfin id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's response.</returns>
    public Task<HttpResponseMessage> OpenLinkImageAsync(string sourceItemId, string kind, int index, int width, string callerServerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceItemId);
        ArgumentNullException.ThrowIfNull(kind);

        return SendPeerStreamAsync(
            "/Sync/Peer/Links/" + Uri.EscapeDataString(sourceItemId) + "/Images/" + Uri.EscapeDataString(kind) + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "?serverId=" + Uri.EscapeDataString(callerServerId ?? string.Empty) + "&width=" + width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            false,
            cancellationToken);
    }

    /// <summary>
    /// Sends one signed stream request with no retry, since a stream's caller falls back to another server itself.
    /// </summary>
    private async Task<HttpResponseMessage> SendPeerStreamAsync(string path, string? range, bool head, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, _serverUrl + path);
        request.Options.Set(BaseRetryHandler.NoRetry, true);
        AddAuthorizationHeader(request);
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.VersionHeader, _pluginVersion);
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.ProtocolHeader, Services.Queue.HintProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.MinProtocolHeader, Services.Queue.HintProtocol.MinProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var secret = Pairing?.CurrentSecret();
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.PairingHeader, secret);
        }

        if (!string.IsNullOrEmpty(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the peer's links, each with the path its copy would have.
    /// Throws on any answer but success or a 404, since a refusal or an error does not say the peer has no links.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's links, or null when the peer has no links route.</returns>
    public async Task<PeerLinksResponse?> GetPeerLinksAsync(CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<PeerLinksResponse>(HttpMethod.Get, "/Sync/Peer/Links", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);

        // Only a missing route says the peer has no links.
        if (status == 404)
        {
            _logger.LogDebug("The peer has no links route: {Body}", FormatUtilities.SanitizeForLog(body));
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the links request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerLinksResponse();
    }

    /// <summary>
    /// Reads what left the peer's library since a time, and throws on a server error, which is no answer.
    /// </summary>
    /// <param name="since">The time to ask from, in UTC.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's removals, or null when the peer has no such route or refuses, so a full listing finds them.</returns>
    public async Task<PeerChangesResponse?> GetPeerChangesAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var path = "/Sync/Peer/Changes?since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        var (status, parsed, body) = await PeerRequestAsync<PeerChangesResponse>(HttpMethod.Get, path, null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is >= 400 and < 500)
        {
            _logger.LogDebug("The peer does not say what it removed ({Status}): {Body}", status, FormatUtilities.SanitizeForLog(body));
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the changes request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Reads the copies the peer downloaded from other servers, each with its origin, and throws on a server error, which is no answer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The peer's copies, or null when the peer has no such route or refuses, so every file is taken as its own.</returns>
    public async Task<PeerCopiesResponse?> GetPeerCopiesAsync(CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<PeerCopiesResponse>(HttpMethod.Get, "/Sync/Peer/Copies", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is >= 400 and < 500)
        {
            _logger.LogDebug("The peer does not say where its copies came from ({Status}): {Body}", status, FormatUtilities.SanitizeForLog(body));
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the copies request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerCopiesResponse();
    }

    /// <summary>
    /// Makes one signed call to the peer's Server Sync routes, parsing a success and reading a failure's body as text.
    /// A peer that answers "not paired" is asked to pair and the request goes once more.
    /// </summary>
    /// <typeparam name="TResponse">The shape of a successful answer.</typeparam>
    /// <param name="method">GET or POST.</param>
    /// <param name="pathAndQuery">The path under the server URL.</param>
    /// <param name="body">The request body to send as JSON, or null for none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="pairOnRefusal">Whether a "not paired" answer leads to pairing and one more try.</param>
    /// <returns>The status, the parsed answer on success, and the body text on failure.</returns>
    private async Task<(int Status, TResponse? Parsed, string? Body)> PeerRequestAsync<TResponse>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken, bool pairOnRefusal = true)
        where TResponse : class
    {
        var answer = await PeerRequestOnceAsync<TResponse>(method, pathAndQuery, body, cancellationToken).ConfigureAwait(false);
        if (answer.Status != Services.Queue.HintProtocol.UnpairedStatus || !pairOnRefusal || Pairing is null)
        {
            return answer;
        }

        _logger.LogInformation("The peer at {Url} does not hold a pairing for this server, pairing now", FormatUtilities.SanitizeForLog(_serverUrl));
        var link = await GetPeerLinkAsync(Pairing.ThisServerId, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return answer;
        }

        return await PeerRequestOnceAsync<TResponse>(method, pathAndQuery, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes one call under its own deadline, reporting a passed deadline as a failed request.
    /// Only the caller's own token cancels, since hint workers treat a cancellation as shutdown.
    /// </summary>
    private async Task<(int Status, TResponse? Parsed, string? Body)> PeerRequestOnceAsync<TResponse>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Services.Queue.HintProtocol.PeerCallTimeout);
        try
        {
            return await PeerRequestCoreAsync<TResponse>(method, pathAndQuery, body, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"The peer at {_serverUrl} did not finish answering {pathAndQuery.Split('?')[0]} within {Services.Queue.HintProtocol.PeerCallTimeout.TotalMinutes:0} minutes", ex);
        }
    }

    /// <summary>
    /// Builds, signs, and sends one peer request, reading no more of the answer than the plugin is prepared to hold.
    /// </summary>
    private async Task<(int Status, TResponse? Parsed, string? Body)> PeerRequestCoreAsync<TResponse>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var request = new HttpRequestMessage(method, _serverUrl + pathAndQuery);
        AddAuthorizationHeader(request);
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.VersionHeader, _pluginVersion);
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.ProtocolHeader, Services.Queue.HintProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.MinProtocolHeader, Services.Queue.HintProtocol.MinProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var secret = Pairing?.CurrentSecret();
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.TryAddWithoutValidation(Services.Queue.HintProtocol.PairingHeader, secret);
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body, PeerJsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var content = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (status, null, RefusalText(content));
        }

        if (content.Length == 0)
        {
            return (status, null, null);
        }

        return (status, System.Text.Json.JsonSerializer.Deserialize<TResponse>(content, PeerJsonOptions), null);
    }

    /// <summary>
    /// Cuts a refusal's body, which the peer wrote, to a short single line fit for logs, row reasons, and the dashboard.
    /// </summary>
    private static string RefusalText(byte[] content)
    {
        const int Most = 2048;
        var text = System.Text.Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, Most * 4));
        var line = new System.Text.StringBuilder(Math.Min(text.Length, Most));
        foreach (var c in text)
        {
            if (line.Length >= Most)
            {
                break;
            }

            line.Append(char.IsControl(c) ? ' ' : c);
        }

        return line.ToString();
    }

    /// <summary>
    /// Reads a response body, throwing once it passes the largest answer the plugin reads from a peer.
    /// </summary>
    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var cap = Services.Queue.HintProtocol.MaxPeerResponseBytes;
        if (response.Content.Headers.ContentLength > cap)
        {
            throw new HttpRequestException($"The peer's answer is {response.Content.Headers.ContentLength} bytes, more than the {cap} this plugin reads");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new System.IO.MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (buffer.Length > cap)
                {
                    throw new HttpRequestException($"The peer's answer passed {cap} bytes, more than this plugin reads");
                }
            }

            return buffer.ToArray();
        }
    }
}
