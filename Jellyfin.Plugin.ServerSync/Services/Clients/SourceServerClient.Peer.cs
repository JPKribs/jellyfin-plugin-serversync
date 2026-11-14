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

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Calls to a peer that runs Server Sync: capabilities, pairing, hints, history negotiation, and versions.
/// </summary>
public partial class SourceServerClient
{
    // Jellyfin serializes with Pascal case names and enums as strings. Reading is case insensitive so
    // either casing on the wire is accepted.
    private static readonly System.Text.Json.JsonSerializerOptions PeerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Asks the source whether it runs Server Sync and what it can negotiate. Returns null when the
    /// source answers 404, which means the plugin is not installed there.
    /// Any other failure is raised with its status, so a caller can tell a refused key from a peer
    /// that is down.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's capabilities, or null when the source has no peer endpoint.</returns>
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

    public async Task<(string Code, string Message)> CheckSyncAccessAsync(CancellationToken cancellationToken = default)
    {
        int status;
        string? body;
        try
        {
            (status, _, body) = await PeerRequestAsync<PeerCapabilities>(HttpMethod.Get, "/Sync/Peer/Capabilities", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ("Unknown", "Could not reach Server Sync there: " + ex.Message);
        }

        if (status is >= 200 and < 300)
        {
            return ("Allowed", "This key may use the sync endpoints there");
        }

        if (status == 404)
        {
            return ("NotInstalled", "Server Sync is not installed there");
        }

        if (status == 426)
        {
            return ("VersionMismatch", body ?? "Server Sync there speaks a protocol this server does not");
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
                    return (code.GetString() ?? "Refused", message.GetString() ?? "Refused");
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return ("Refused", $"Server Sync there refused this key ({status})");
    }

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
            _logger.LogDebug("The peer's Server Sync did not list its playlists ({Status}): {Body}", status, body);
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the playlists request: {body}", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerPlaylistsResponse();
    }

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

    public Task<List<BaseItemDto>> GetPlaylistEntriesAsync(Guid playlistId, Guid userId, CancellationToken cancellationToken = default)
        => GetPlaylistItemsAsync(playlistId, userId, cancellationToken, distinct: false);

    /// <summary>
    /// Sends merged history states to the source's Server Sync plugin. The source writes each one only
    /// if its live state still matches what this server expected, and reports the outcome per entry.
    /// </summary>
    /// <param name="peerRequest">The entries to negotiate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
    /// <param name="queueRequest">The hints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status code, the parsed answer on 200, and the body text otherwise.</returns>
    public async Task<(int StatusCode, Models.Queue.QueueResponse? Response, string? Body)> SendHintsAsync(Models.Queue.QueueRequest queueRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queueRequest);
        var (status, parsed, body) = await PeerRequestAsync<Models.Queue.QueueResponse>(HttpMethod.Post, "/Sync/Peer/Queue", queueRequest, cancellationToken).ConfigureAwait(false);
        return status == 200 ? (status, parsed ?? new Models.Queue.QueueResponse(), null) : (status, null, body);
    }

    /// <summary>Asks the peer whether it lists this server, and how.</summary>
    /// <param name="thisServerId">This server's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The listing, or null when the peer has no Server Sync that can answer.</returns>
    public async Task<Models.Peer.PeerLinkResponse?> GetPeerLinkAsync(string thisServerId, CancellationToken cancellationToken = default)
    {
        // The peer answers this by handing over its secret before it returns, so the secret is only
        // taken while this call is open.
        using var expecting = Pairing?.Expect();
        var (_, parsed, _) = await PeerRequestAsync<Models.Peer.PeerLinkResponse>(HttpMethod.Get, "/Sync/Peer/Link?serverId=" + Uri.EscapeDataString(thisServerId ?? string.Empty), null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>Hands the peer the secret it must present on requests that name it as the sender to this server.</summary>
    /// <param name="pairRequest">This server's id and the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status the peer answered and its body when it refused.</returns>
    public async Task<(int StatusCode, string? Body)> PairAsync(Models.Peer.PairRequest pairRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairRequest);
        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Pair", pairRequest, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        return (status, body);
    }

    /// <summary>Reads one person by name with the fields the people module compares.</summary>
    /// <param name="name">The person's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>Asks the peer which versions it holds for a batch of its own keys.</summary>
    /// <param name="versionsRequest">The kind and keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>Tells the origin of some hints that they are done.</summary>
    /// <param name="completeRequest">The finished hints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the origin accepted the report.</returns>
    public async Task<bool> CompleteHintsAsync(Models.Queue.CompleteRequest completeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completeRequest);
        var (status, _, _) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Complete", completeRequest, cancellationToken).ConfigureAwait(false);
        return status is >= 200 and < 300;
    }

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

    public string ServerUrl => _serverUrl;

    /// <summary>Reads the hints this server sent the peer that it still holds, and those it finished.</summary>
    /// <param name="fromServerId">This server's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status, or null when it could not be read.</returns>
    public async Task<Models.Queue.QueueStatusResponse?> GetPeerQueueStatusAsync(string fromServerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromServerId);
        var (_, parsed, _) = await PeerRequestAsync<Models.Queue.QueueStatusResponse>(HttpMethod.Get, "/Sync/Peer/Status?serverId=" + Uri.EscapeDataString(fromServerId), null, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    /// <summary>
    /// Opens a stream of one of the peer's files for a link here, passing <paramref name="range"/> on.
    /// The caller owns the response and reads the body as it arrives. A peer that no longer holds this
    /// server's pairing is paired again once.
    /// </summary>
    /// <param name="sourceItemId">The peer's id of the item.</param>
    /// <param name="callerServerId">This server's Jellyfin id.</param>
    /// <param name="grant">Who the stream is for.</param>
    /// <param name="sourceUserId">The peer's user this server maps the user to, or null.</param>
    /// <param name="range">The Range header to pass on, or null.</param>
    /// <param name="head">Whether to ask for the headers only.</param>
    /// <param name="fill">Whether this downloads the file into this server's link cache, rather than playing it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's answer.</returns>
    public async Task<HttpResponseMessage> OpenPeerStreamAsync(string sourceItemId, string callerServerId, Links.LinkGrant grant, string? sourceUserId, string? range, bool head, bool fill, CancellationToken cancellationToken = default)
    {
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
    /// Asks the peer whether a user here may stream one of its files through a link, before playback
    /// starts. A refusal carries the peer's reason.
    /// </summary>
    /// <param name="request">The item and user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's answer.</returns>
    public async Task<LinkStreamCheckResponse> CheckLinkStreamAsync(LinkStreamCheckRequest request, CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<LinkStreamCheckResponse>(HttpMethod.Post, "/Sync/Peer/Stream/Check", request, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300 || parsed is null)
        {
            return new LinkStreamCheckResponse { Allowed = false, Reason = $"the source answered {status}: {body}" };
        }

        return parsed;
    }

    /// <summary>Reports playback of a link to the peer that holds the file. Throws when the peer refuses.</summary>
    /// <param name="report">The report.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReportLinkPlaybackAsync(PeerPlaybackReport report, CancellationToken cancellationToken = default)
    {
        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Playback", report, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to a playback report: {body}", null, (System.Net.HttpStatusCode)status);
        }
    }

    /// <summary>Passes a remote control command to the peer for a viewer of a link there. Throws when refused.</summary>
    /// <param name="request">The command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendLinkControlAsync(PeerControlRequest request, CancellationToken cancellationToken = default)
    {
        var (status, _, body) = await PeerRequestAsync<object>(HttpMethod.Post, "/Sync/Peer/Control", request, cancellationToken).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to a remote control command: {body}", null, (System.Net.HttpStatusCode)status);
        }
    }

    /// <summary>
    /// Asks the peer about files this server holds links to. Null when the peer runs a Server Sync
    /// from before it answered this. Throws with the peer's reason when it refuses, such as when
    /// linking is not allowed for this server there.
    /// </summary>
    /// <param name="request">The items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One answer per item, or null.</returns>
    public async Task<PeerLinkDescribeResponse?> DescribeLinksAsync(PeerLinkDescribeRequest request, CancellationToken cancellationToken = default)
    {
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
    /// Opens an image the peer made for a file this server links to: a chapter's image or a trickplay
    /// tile. The caller disposes the response.
    /// </summary>
    /// <param name="sourceItemId">The peer's id of the item.</param>
    /// <param name="kind">Chapter or Trickplay.</param>
    /// <param name="index">The chapter's index, or the tile's.</param>
    /// <param name="width">For a trickplay tile, the width of its set.</param>
    /// <param name="callerServerId">This server's Jellyfin id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's response.</returns>
    public Task<HttpResponseMessage> OpenLinkImageAsync(string sourceItemId, string kind, int index, int width, string callerServerId, CancellationToken cancellationToken = default)
        => SendPeerStreamAsync(
            "/Sync/Peer/Links/" + Uri.EscapeDataString(sourceItemId) + "/Images/" + Uri.EscapeDataString(kind) + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "?serverId=" + Uri.EscapeDataString(callerServerId ?? string.Empty) + "&width=" + width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            false,
            cancellationToken);

    private async Task<HttpResponseMessage> SendPeerStreamAsync(string path, string? range, bool head, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, _serverUrl + path);

        // A stream's caller falls back to another server itself, so this server is asked once.
        request.Options.Set(TransientRetryHandler.NoRetry, true);
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
    /// Reads the peer's links, each with the path its full copy would have. Null when the peer refuses or
    /// has no such route, so a caller goes on with paths as they arrive. Throws on a server error.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's links, or null.</returns>
    public async Task<PeerLinksResponse?> GetPeerLinksAsync(CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<PeerLinksResponse>(HttpMethod.Get, "/Sync/Peer/Links", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);

        // Only a missing route says the peer has no links: it runs a version from before them. A refusal,
        // a protocol mismatch, or a server error is no answer, so it is raised and asked again, and until
        // then the peer's .strm files are left alone.
        if (status == 404)
        {
            _logger.LogDebug("The peer has no links route: {Body}", body);
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the links request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerLinksResponse();
    }

    /// <summary>
    /// Reads what left the peer's library since a time. Null when the peer has no such route or
    /// refuses, in which case removals are found the way they always were, by a full listing. Throws
    /// on a server error, which is no answer.
    /// </summary>
    /// <param name="since">The time to ask from, in UTC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's removals, or null.</returns>
    public async Task<PeerChangesResponse?> GetPeerChangesAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var path = "/Sync/Peer/Changes?since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        var (status, parsed, body) = await PeerRequestAsync<PeerChangesResponse>(HttpMethod.Get, path, null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is >= 400 and < 500)
        {
            _logger.LogDebug("The peer does not say what it removed ({Status}): {Body}", status, body);
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the changes request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed;
    }

    /// <summary>
    /// Reads the copies the peer downloaded from other servers, each with its origin. Null when the peer
    /// has no such route, as a server without Server Sync or a version from before origins has none,
    /// and when it refuses the request, as it does for a key it will not answer peer calls for. Such a
    /// server is read as it always was, with every file taken as its own. Throws on a server error,
    /// which is no answer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The peer's copies, or null.</returns>
    public async Task<PeerCopiesResponse?> GetPeerCopiesAsync(CancellationToken cancellationToken = default)
    {
        var (status, parsed, body) = await PeerRequestAsync<PeerCopiesResponse>(HttpMethod.Get, "/Sync/Peer/Copies", null, cancellationToken, pairOnRefusal: false).ConfigureAwait(false);
        if (status is >= 400 and < 500)
        {
            _logger.LogDebug("The peer does not say where its copies came from ({Status}): {Body}", status, body);
            return null;
        }

        if (status is < 200 or >= 300)
        {
            throw new HttpRequestException($"The peer answered {status} to the copies request", null, (System.Net.HttpStatusCode)status);
        }

        return parsed ?? new PeerCopiesResponse();
    }

    /// <summary>
    /// One call to the peer's Server Sync endpoints. Builds the request, signs it, sends it, and on a
    /// success parses the answer, otherwise reads the body as text. Every peer method is a short rule
    /// over this, so the transport and its error handling live in one place.
    /// </summary>
    /// <typeparam name="TResponse">The shape of a successful answer.</typeparam>
    /// <param name="method">GET or POST.</param>
    /// <param name="pathAndQuery">The path under the server URL.</param>
    /// <param name="body">The request body to send as JSON, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="pairOnRefusal">Whether a "not paired" answer leads to pairing and one more try.</param>
    /// <returns>The status, the parsed answer on success, and the body text on failure.</returns>
    // A peer that answers "not paired" is asked to pair, which lands the new secret here before the
    // link call returns, and the request goes once more. Reads are capped so a peer cannot hand back
    // more than the plugin is prepared to hold.
    private async Task<(int Status, TResponse? Parsed, string? Body)> PeerRequestAsync<TResponse>(HttpMethod method, string pathAndQuery, object? body, CancellationToken cancellationToken, bool pairOnRefusal = true)
        where TResponse : class
    {
        var answer = await PeerRequestOnceAsync<TResponse>(method, pathAndQuery, body, cancellationToken).ConfigureAwait(false);
        if (answer.Status != Services.Queue.HintProtocol.UnpairedStatus || !pairOnRefusal || Pairing is null)
        {
            return answer;
        }

        // The link call makes the peer issue a secret, or note that it cannot, and either way it
        // decides afresh on the next request.
        _logger.LogInformation("The peer at {Url} does not hold a pairing for this server, pairing now", FormatUtilities.SanitizeForLog(_serverUrl));
        var link = await GetPeerLinkAsync(Pairing.ThisServerId, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return answer;
        }

        return await PeerRequestOnceAsync<TResponse>(method, pathAndQuery, body, cancellationToken).ConfigureAwait(false);
    }

    // One call with its own deadline over sending and reading. A deadline that passes is reported as a
    // failed request rather than a cancellation, since only the caller's own token means "stop": the
    // hint workers treat a cancellation as shutdown, and one slow peer must never stop them.
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

    // A refusal's body ends up in logs, row reasons and the dashboard, and the peer wrote it. It is
    // cut to a short single line, which still holds every refusal Server Sync itself sends.
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
