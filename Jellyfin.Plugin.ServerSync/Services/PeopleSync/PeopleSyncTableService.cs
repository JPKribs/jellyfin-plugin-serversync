using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Per-modal helpers for People Sync. The build/merge logic lives in
/// <see cref="PeopleSyncMergeService"/>; this service owns operations that
/// need <see cref="ILibraryManager"/> or per-modal HTTP — refreshing the
/// local snapshot for the UI, and enriching the source image manifest with
/// real sizes.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class PeopleSyncTableService
{
    private readonly ILogger<PeopleSyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleSyncTableService"/> class.
    /// </summary>
    public PeopleSyncTableService(
        ILogger<PeopleSyncTableService> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Re-reads the local side of a person record so the modal shows live
    /// data rather than the snapshot captured at the last people refresh.
    /// Call this from per-item endpoints — without it, a successful Sync
    /// apply updates local state in Jellyfin but the modal keeps showing
    /// the pre-sync local blob until the user re-runs the full Refresh.
    /// Source-side fields (and all hashes) are left untouched.
    /// </summary>
    public void RefreshLocalSnapshot(PeopleSyncItem record, bool syncImages)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrEmpty(record.PersonName)) return;

        var personStub = _libraryManager.GetPerson(record.PersonName);
        if (personStub == null) return;

        var localPerson = _libraryManager.GetItemById(personStub.Id);
        if (localPerson == null) return;

        record.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(localPerson);

        if (syncImages)
        {
            var (_, localImg) = PeopleSyncMergeService.PopulateImageData(null, localPerson);
            record.Images.Local = localImg;
        }
    }

    /// <summary>
    /// Enriches the source-side image manifest with real Size / Width /
    /// Height fetched from the source server. The refresh builds the
    /// manifest from <c>BaseItemDto.ImageTags</c> (tag-only) for
    /// performance, so without enrichment the People modal renders Source
    /// as "1 (0 B)" — looks like a sync failure even when nothing's wrong.
    /// One HTTP call per modal open.
    /// </summary>
    public async Task EnrichSourceImageSizesAsync(
        PeopleSyncItem record,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrEmpty(record.SourcePersonId)) return;
        if (!Guid.TryParse(record.SourcePersonId, out var sourcePersonId)) return;

        record.Images.Source = await ImageManifestEnricher.EnrichAsync(
            record.Images.Source,
            sourcePersonId,
            client,
            _logger,
            record.PersonName ?? record.SourcePersonId,
            cancellationToken).ConfigureAwait(false);
    }
}
