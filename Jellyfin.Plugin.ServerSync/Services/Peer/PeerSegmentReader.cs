using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
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
            IReadOnlyCollection<MediaSegmentDto> others = Array.Empty<MediaSegmentDto>();
            SourceMediaSegments? row = null;
            if (segments.Count > 0)
            {
                others = (await _segments.GetSegmentsAsync(item, null, PeerSegmentProvider.OthersOnly(options), filterByProvider: true).ConfigureAwait(false)).ToList();
                if (segments.Count > others.Count)
                {
                    row = _store.Get(item.Id.ToString("N", CultureInfo.InvariantCulture));
                }
            }

            var tagged = TagOrigins(
                segments.Select(s => new PeerSegment { Type = s.Type.ToString(), StartTicks = s.StartTicks, EndTicks = s.EndTicks }).ToList(),
                others.Select(s => new PeerSegment { Type = s.Type.ToString(), StartTicks = s.StartTicks, EndTicks = s.EndTicks }).ToList(),
                row);
            answer.SegmentOrigins = true;
            answer.OriginServerId = tagged.Where(s => s.OriginServerId is not null).Select(s => s.OriginServerId).FirstOrDefault();
            answer.Segments.AddRange(tagged);
        }

        return response;
    }

    /// <summary>
    /// Names the server each of a file's segments was first made on. A segment another provider gave the file was made here and names none.
    /// A segment Server Sync gave it names the origin the local segment table kept for it, matched by type and position, then by type and start,
    /// since Jellyfin may have cut its end to the file's runtime. Failing that it takes the row's origin, or the origin of the row's segments of its type.
    /// </summary>
    /// <param name="segments">Every segment Jellyfin holds for the file.</param>
    /// <param name="others">The segments providers other than Server Sync gave the file.</param>
    /// <param name="row">What the local segment table keeps for the file, or null.</param>
    /// <returns>The segments, each with its origin.</returns>
    public static List<PeerSegment> TagOrigins(IReadOnlyList<PeerSegment> segments, IReadOnlyList<PeerSegment> others, SourceMediaSegments? row)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(others);
        var unclaimed = others.ToList();
        var kept = row?.Segments?.Where(s => s is not null).ToList() ?? new List<PeerSegment>();
        var result = new List<PeerSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var tagged = new PeerSegment { Type = segment.Type, StartTicks = segment.StartTicks, EndTicks = segment.EndTicks };
            result.Add(tagged);
            var own = unclaimed.FindIndex(o => Same(o, segment) && o.EndTicks == segment.EndTicks);
            if (own >= 0)
            {
                unclaimed.RemoveAt(own);
                continue;
            }

            var match = kept.Find(k => Same(k, segment) && k.EndTicks == segment.EndTicks)
                ?? kept.Find(k => Same(k, segment));
            tagged.OriginServerId = match?.OriginServerId
                ?? row?.OriginServerId
                ?? kept.Where(k => string.Equals(k.Type, segment.Type, StringComparison.OrdinalIgnoreCase)).Select(k => k.OriginServerId).FirstOrDefault(o => o is not null)
                ?? kept.Select(k => k.OriginServerId).FirstOrDefault(o => o is not null);
        }

        return result;

        static bool Same(PeerSegment a, PeerSegment b)
            => string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase) && a.StartTicks == b.StartTicks;
    }
}
