using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.PeopleSync;

/// <summary>
/// Refreshes one person row's local side and source image sizes for the dashboard.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class PeopleSyncTableService
{
    private readonly ILogger<PeopleSyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleSyncTableService"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Finds the local person.</param>
    public PeopleSyncTableService(
        ILogger<PeopleSyncTableService> logger,
        ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(libraryManager);
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Reads the local side of a person row again, so the dashboard shows live values and not those of the last scan.
    /// The source side and every hash are left alone.
    /// </summary>
    /// <param name="record">The row whose local blobs are replaced.</param>
    /// <param name="syncImages"><c>true</c> to re-read the images.</param>
    public void RefreshLocalSnapshot(PeopleSyncItem record, bool syncImages)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrEmpty(record.PersonName)) return;

        // The stored id finds the person even when the name's casing differs.
        var localPerson = Guid.TryParse(record.LocalPersonId, out var knownId)
            ? _libraryManager.GetItemById(knownId)
            : null;
        if (localPerson == null)
        {
            var personStub = _libraryManager.GetPerson(record.PersonName);
            if (personStub == null) return;

            localPerson = _libraryManager.GetItemById(personStub.Id);
            if (localPerson == null) return;
        }

        record.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(localPerson);

        if (syncImages)
        {
            record.Images.Local = ImageUtilities.BuildLocalManifest(localPerson, PeopleSyncMergeService.ImageTypes, record.Images.Local);
        }
    }

    /// <summary>
    /// Adds each image's size and dimensions to a row's source manifest with one call to the source.
    /// A scan stores tags alone, which the dashboard would show as a size of 0.
    /// </summary>
    /// <param name="record">The row whose source image manifest is sized.</param>
    /// <param name="client">The client for the source server that holds the images.</param>
    /// <param name="cancellationToken">Cancels the call to the source.</param>
    /// <returns>A task that completes when the manifest is sized.</returns>
    public async Task EnrichSourceImageSizesAsync(
        PeopleSyncItem record,
        SourceServerClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        ArgumentNullException.ThrowIfNull(record);
        if (!Guid.TryParse(record.SourcePersonId, out var sourcePersonId)) return;

        record.Images.Source = await ImageUtilities.EnrichAsync(
            record.Images.Source,
            sourcePersonId,
            client,
            _logger,
            record.PersonName ?? record.SourcePersonId,
            cancellationToken).ConfigureAwait(false);
    }
}
