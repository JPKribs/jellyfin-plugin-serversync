using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using LocalImageType = MediaBrowser.Model.Entities.ImageType;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the people sync table with source and local servers.
/// Derives the person list from synced metadata items rather than the global /Persons endpoint.
/// </summary>
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

        record.Metadata.Local = BuildLocalMetadata(localPerson);

        if (syncImages)
        {
            var (_, localImg) = PopulateImageData(null, localPerson);
            record.Images.Local = localImg;
        }
    }

    /// <summary>
    /// Enriches the source-side image manifest with real Size / Width /
    /// Height fetched from the source server. The refresh task builds the
    /// source manifest from <c>BaseItemDto.ImageTags</c> for performance
    /// (tag-only — no byte size or pixel dimensions), so the People modal
    /// without enrichment renders Source as <c>"1 (0 B)"</c> while Local
    /// has a real KB value, which looks like a sync failure even when
    /// nothing's wrong.
    /// <para>
    /// Per-modal-open: one HTTP call to <c>/Items/{id}/Images</c> on source.
    /// Best-effort — failure leaves the original tag-only blob in place and
    /// logs at Debug. Mutates <paramref name="record"/>.<see cref="PeopleSyncItem.Images"/>.<see cref="SyncableValue{T}.Source"/>
    /// in-memory only; not persisted, so the comparator hashes used by the
    /// next Refresh remain consistent with the tag-based source manifest.
    /// </para>
    /// </summary>
    public async System.Threading.Tasks.Task EnrichSourceImageSizesAsync(
        PeopleSyncItem record,
        SourceServerClient client,
        System.Threading.CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(client);

        if (string.IsNullOrEmpty(record.Images.Source)) return;
        if (string.IsNullOrEmpty(record.SourcePersonId)) return;
        if (!Guid.TryParse(record.SourcePersonId, out var sourcePersonId)) return;

        Dictionary<string, List<ImageInfoDto>>? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(record.Images.Source);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not deserialize source image manifest for person {Name}", record.PersonName);
            return;
        }

        if (manifest == null || manifest.Count == 0) return;

        List<Jellyfin.Sdk.Generated.Models.ImageInfo>? sourceInfo;
        try
        {
            sourceInfo = await client.GetItemImageInfoAsync(sourcePersonId, cancellationToken).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Source image-info enrichment failed for person {Name}", record.PersonName);
            return;
        }

        if (sourceInfo == null || sourceInfo.Count == 0) return;

        var byKey = new Dictionary<(string Type, int Index), Jellyfin.Sdk.Generated.Models.ImageInfo>();
        foreach (var info in sourceInfo)
        {
            var typeName = info.ImageType?.ToString();
            if (string.IsNullOrEmpty(typeName)) continue;
            byKey[(typeName, info.ImageIndex ?? 0)] = info;
        }

        var anyChanged = false;
        foreach (var kvp in manifest)
        {
            for (var i = 0; i < kvp.Value.Count; i++)
            {
                var entry = kvp.Value[i];
                if (byKey.TryGetValue((kvp.Key, entry.ImageIndex), out var info))
                {
                    if (info.Size.HasValue) entry.Size = info.Size.Value;
                    if (info.Width.HasValue) entry.Width = info.Width.Value;
                    if (info.Height.HasValue) entry.Height = info.Height.Value;
                    anyChanged = true;
                }
            }
        }

        if (anyChanged)
        {
            record.Images.Source = JsonSerializer.Serialize(manifest);
        }
    }

    /// <summary>
    /// Builds a metadata JSON blob from a source BaseItemDto (person).
    /// Matches the same pattern as MetadataSyncTableService.SetMetadataFieldValues.
    /// </summary>
    public static string BuildSourceMetadata(BaseItemDto sourcePerson)
    {
        // Unwrap Kiota's UntypedNode wrappers — calling .ToString() directly on
        // an AdditionalData entry yields the type name, not the wrapped value,
        // which made every ProviderId look like "UntypedString" and produced a
        // permanent IMDB desync in the modal.
        var sourceProviderIds = sourcePerson.ProviderIds?.AdditionalData?
            .Select(kvp => (kvp.Key, Value: MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value)))
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        // Sort all order-insensitive arrays so source and local serialize
        // identically. The JSON comparator compares element-by-element and
        // would otherwise flag differently-ordered-but-same-content arrays
        // as "changed" forever, even after a successful apply.
        var metadata = new Dictionary<string, object?>
        {
            ["Name"] = sourcePerson.Name,
            ["OriginalTitle"] = sourcePerson.OriginalTitle,
            // SortName excluded — server-derived from Name, never matches
            // across servers. ForcedSortName is the user override and IS
            // synced.
            ["ForcedSortName"] = sourcePerson.ForcedSortName,
            ["Overview"] = sourcePerson.Overview,
            ["PremiereDate"] = sourcePerson.PremiereDate,
            ["EndDate"] = sourcePerson.EndDate,
            ["ProductionYear"] = sourcePerson.ProductionYear,
            ["ProductionLocations"] = NormalizeStringArray(sourcePerson.ProductionLocations),
            ["Tags"] = NormalizeStringArray(sourcePerson.Tags),
            ["ProviderIds"] = sourceProviderIds,
            ["LockedFields"] = sourcePerson.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = sourcePerson.LockData ?? false
        };

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Builds a metadata JSON blob from a local BaseItem (person).
    /// Matches the same pattern as MetadataSyncTableService.SetMetadataFieldValues.
    /// </summary>
    public static string BuildLocalMetadata(BaseItem localPerson)
    {
        var localProviderIds = localPerson.ProviderIds?
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var metadata = new Dictionary<string, object?>
        {
            ["Name"] = localPerson.Name,
            ["OriginalTitle"] = localPerson.OriginalTitle,
            // SortName excluded — see source-side comment.
            ["ForcedSortName"] = localPerson.ForcedSortName,
            ["Overview"] = localPerson.Overview,
            ["PremiereDate"] = localPerson.PremiereDate,
            ["EndDate"] = localPerson.EndDate,
            ["ProductionYear"] = localPerson.ProductionYear,
            ["ProductionLocations"] = NormalizeStringArray(localPerson.ProductionLocations),
            ["Tags"] = NormalizeStringArray(localPerson.Tags),
            ["ProviderIds"] = localProviderIds,
            ["LockedFields"] = localPerson.LockedFields?.Select(f => f.ToString())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ["LockData"] = localPerson.IsLocked
        };

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Collapses the three storage shapes Jellyfin returns for "no value"
    /// string arrays — <c>null</c>, <c>[]</c>, and <c>[""]</c> — into a single
    /// canonical <c>null</c>. Without this, an item whose source has
    /// <c>ProductionLocations: null</c> but whose local persists as
    /// <c>[""]</c> (empty-string-in-array, observed on Person rows after
    /// UpdateToRepositoryAsync) compares unequal forever and verification
    /// keeps Erroring the row. Filtering whitespace and emitting null when
    /// empty makes both sides round-trip identically.
    /// </summary>
    private static string[]? NormalizeStringArray(IReadOnlyList<string>? source)
    {
        if (source == null)
        {
            return null;
        }

        var filtered = source
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return filtered.Length == 0 ? null : filtered;
    }

    /// <summary>
    /// Builds the source-side and local-side image manifests for a person.
    /// The source side is built from <see cref="BaseItemDto.ImageTags"/>
    /// returned by the bulk <c>/Persons</c> fetch — no per-person HTTP call.
    /// Tags are unwrapped via <see cref="MediaItemUtilities.UnwrapKiotaPrimitive"/>
    /// (otherwise they'd come out as Kiota's <c>UntypedString</c> type name).
    /// Caller pipes the result through <see cref="SyncableValue{T}.UpdateSource"/>
    /// so the comparator handles hashing uniformly.
    /// </summary>
    public (string? SourceImagesValue, string? LocalImagesValue) PopulateImageData(
        BaseItemDto? sourcePerson,
        BaseItem? localPerson)
    {
        string? sourceImagesValue = null;
        string? localImagesValue = null;

        if (sourcePerson?.ImageTags?.AdditionalData != null)
        {
            var sourceImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            foreach (var kvp in sourcePerson.ImageTags.AdditionalData)
            {
                var tag = MediaItemUtilities.UnwrapKiotaPrimitive(kvp.Value);
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                sourceImagesByType[kvp.Key] = new List<ImageInfoDto>
                {
                    new ImageInfoDto
                    {
                        ImageType = kvp.Key,
                        ImageIndex = 0,
                        Tag = tag
                    }
                };
            }

            if (sourceImagesByType.Count > 0)
            {
                sourceImagesValue = JsonSerializer.Serialize(sourceImagesByType);
            }
        }

        // Local images - people typically only have Primary images
        if (localPerson != null)
        {
            var localImagesByType = new Dictionary<string, List<ImageInfoDto>>();
            var images = localPerson.GetImages(LocalImageType.Primary).ToList();
            if (images.Count > 0)
            {
                var imageInfoList = new List<ImageInfoDto>();
                for (int idx = 0; idx < images.Count; idx++)
                {
                    var img = images[idx];
                    long fileSize = 0;

                    if (!string.IsNullOrEmpty(img.Path) && File.Exists(img.Path))
                    {
                        try
                        {
                            fileSize = new FileInfo(img.Path).Length;
                        }
                        catch (IOException)
                        {
                            // Ignore file access errors
                        }
                    }

                    imageInfoList.Add(new ImageInfoDto
                    {
                        ImageType = LocalImageType.Primary.ToString(),
                        ImageIndex = idx,
                        Size = fileSize,
                        Width = img.Width,
                        Height = img.Height
                    });
                }

                localImagesByType[LocalImageType.Primary.ToString()] = imageInfoList;
            }

            if (localImagesByType.Count > 0)
            {
                localImagesValue = JsonSerializer.Serialize(localImagesByType);
            }
        }

        return (sourceImagesValue, localImagesValue);
    }
}
