using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Reads the media segments of a batch of this server's items for a peer that pulls from it, with the server they were first made on
/// when the file shows segments Server Sync copied here, so a server never takes back segments it made itself.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerSegmentReader
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSegmentManager _segments;
    private readonly MediaSegmentStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSegmentReader"/> class.
    /// </summary>
    /// <param name="libraryManager">Finds each item and its library's settings.</param>
    /// <param name="userManager">Finds the user a standard caller is held to.</param>
    /// <param name="segments">Reads the segments Jellyfin holds for an item.</param>
    /// <param name="store">Names the server an item's copied segments were first made on.</param>
    public PeerSegmentReader(ILibraryManager libraryManager, IUserManager userManager, IMediaSegmentManager segments, MediaSegmentStore store)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(store);
        _libraryManager = libraryManager;
        _userManager = userManager;
        _segments = segments;
        _store = store;
    }

    /// <summary>
    /// Reads the segments of each named item. An item that is not here, is not a video or audio file, or that a standard caller may not see is answered as not found.
    /// </summary>
    /// <param name="itemIds">This server's ids of the items.</param>
    /// <param name="scopedUser">The user a standard caller is held to, or null for an administrator or an API key.</param>
    /// <returns>One entry for each id, in order.</returns>
    public async Task<PeerSegmentsResponse> ReadAsync(IReadOnlyList<string> itemIds, Guid? scopedUser)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var user = scopedUser is { } userId ? _userManager.GetUserById(userId) : null;
        var response = new PeerSegmentsResponse();
        foreach (var itemId in itemIds)
        {
            var answer = new PeerSegmentsItem { ItemId = itemId ?? string.Empty };
            response.Items.Add(answer);
            if (!Guid.TryParse(itemId, out var id)
                || _libraryManager.GetItemById(id) is not { } item
                || item is not (Video or MediaBrowser.Controller.Entities.Audio.Audio)
                || (scopedUser is not null && (user is null || !item.IsVisible(user))))
            {
                continue;
            }

            var options = _libraryManager.GetLibraryOptions(item) ?? new MediaBrowser.Model.Configuration.LibraryOptions();
            var segments = (await _segments.GetSegmentsAsync(item, null, options, filterByProvider: true).ConfigureAwait(false)).ToList();
            answer.Found = true;
            answer.Size = item.Size;
            answer.RunTimeTicks = item.RunTimeTicks;

            // Only segments Server Sync gave the file are copies. Any others were made here, whatever the cache still holds.
            if (segments.Count > 0)
            {
                var others = await _segments.GetSegmentsAsync(item, null, PeerSegmentProvider.OthersOnly(options), filterByProvider: true).ConfigureAwait(false);
                if (segments.Count > others.Count())
                {
                    answer.OriginServerId = _store.Get(item.Id.ToString("N", CultureInfo.InvariantCulture))?.OriginServerId;
                }
            }

            answer.Segments.AddRange(segments.Select(s => new PeerSegment { Type = s.Type.ToString(), StartTicks = s.StartTicks, EndTicks = s.EndTicks }));
        }

        return response;
    }
}
