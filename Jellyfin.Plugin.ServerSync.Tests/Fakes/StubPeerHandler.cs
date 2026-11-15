using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A peer over a stub HTTP handler: answers the connection test and the peer playlist listing the
/// way a live server does, so the real client parses them.
/// </summary>
internal sealed class StubPeerHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions PeerJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public HttpStatusCode SystemInfoStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode PlaylistsStatus { get; set; } = HttpStatusCode.OK;

    public PeerPlaylistsResponse Playlists { get; set; } = new();

    public List<string> Paths { get; } = new();

    public static PeerPlaylist Playlist(string name, Guid owner, Guid? id = null, Guid? root = null, bool isCopy = false, params string[] paths) => new()
    {
        Id = (id ?? Guid.NewGuid()).ToString("N"),
        RootId = root?.ToString("N") ?? string.Empty,
        IsCopy = isCopy,
        Name = name,
        MediaType = "Video",
        OwnerUserId = owner.ToString("N"),
        Entries = paths.Select(p => new PeerPlaylistEntry { ItemId = Guid.NewGuid().ToString("N"), Path = p }).ToList()
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);
        HttpResponseMessage response = path switch
        {
            "/System/Info" => SystemInfoStatus == HttpStatusCode.OK
                ? Json("{\"ServerName\":\"peer\",\"Id\":\"0123456789abcdef0123456789abcdef\"}")
                : new HttpResponseMessage(SystemInfoStatus),
            "/Users/Me" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            "/Sync/Peer/Playlists" => PlaylistsStatus == HttpStatusCode.OK
                ? Json(JsonSerializer.Serialize(Playlists, PeerJson))
                : new HttpResponseMessage(PlaylistsStatus) { Content = new StringContent("refused") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
