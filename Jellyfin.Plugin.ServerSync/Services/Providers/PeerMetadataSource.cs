using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using BaseItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;
using ImageInfo = Jellyfin.Sdk.Generated.Models.ImageInfo;
using ItemFields = Jellyfin.Sdk.Generated.Models.ItemFields;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// Finds an item on the sources and reads its metadata and images for the providers.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerMetadataSource
{
    /// <summary>The name of the provider in Jellyfin.</summary>
    public const string ProviderName = "Server Sync";

    /// <summary>The provider id key that pins an item to one server.</summary>
    public const string PinnedProviderKey = "ServerSyncPeerItem";

    /// <summary>How long one server has to answer a lookup.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> ByName = new(StringComparer.Ordinal)
    {
        nameof(Person),
        nameof(MediaBrowser.Controller.Entities.Audio.MusicArtist),
        nameof(MediaBrowser.Controller.Entities.Movies.BoxSet)
    };

    private static readonly ItemFields[] Fields =
    {
        ItemFields.Path,
        ItemFields.DateCreated,
        ItemFields.ProviderIds,
        ItemFields.Overview,
        ItemFields.OriginalTitle,
        ItemFields.SortName,
        ItemFields.ProductionLocations,
        ItemFields.Taglines,
        ItemFields.Settings,
        ItemFields.CustomRating,
        ItemFields.Genres,
        ItemFields.Tags,
        ItemFields.Studios,
        ItemFields.People
    };

    private readonly ParentDirectory _parents;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PeerMetadataSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMetadataSource"/> class.
    /// </summary>
    /// <param name="parents">Lists the sources that can answer a lookup.</param>
    /// <param name="clientFactory">Builds the client that reads from each source.</param>
    /// <param name="libraryManager">Resolves the people a source names.</param>
    /// <param name="logger">Writes why an item had no match.</param>
    public PeerMetadataSource(ParentDirectory parents, ISourceServerClientFactory clientFactory, ILibraryManager libraryManager, ILogger<PeerMetadataSource> logger)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(logger);

        _parents = parents;
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Translates a local path to the path the item has on a source.</summary>
    /// <param name="parent">The source whose library mappings translate the path.</param>
    /// <param name="localPath">The local path, or null.</param>
    /// <returns>The path there, or null when no library mapping holds the path.</returns>
    public static string? ToParentPath(SourceServer parent, string? localPath)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (string.IsNullOrEmpty(localPath) || HintMapping.FindByLocalPath(parent, localPath) is not { } mapping)
        {
            return null;
        }

        var localRoot = mapping.LocalRootPath.TrimEnd('/', '\\');
        var sourceRoot = mapping.SourceRootPath.TrimEnd('/', '\\');
        var separator = sourceRoot.Contains('\\', StringComparison.Ordinal) && !sourceRoot.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
        var relative = localPath.Length > localRoot.Length ? localPath[localRoot.Length..].TrimStart('/', '\\') : string.Empty;
        return relative.Length == 0 ? sourceRoot : sourceRoot + separator + relative.Replace('/', separator).Replace('\\', separator);
    }

    /// <summary>Builds the request that asks a source for an item.</summary>
    /// <param name="parent">The source that will be asked.</param>
    /// <param name="keys">What identifies the item.</param>
    /// <returns>The request, or null when nothing in the keys can be matched there.</returns>
    public static PeerLookupRequest? LookupFor(SourceServer parent, PeerItemKeys keys)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(keys);

        var parentPath = ToParentPath(parent, keys.LocalPath);
        var byName = ByName.Contains(keys.ItemType) && !string.IsNullOrWhiteSpace(keys.Name);
        var ids = Peer.PeerItemLookup.CleanIds(keys.ProviderIds);
        var seriesIds = keys.IndexNumber.HasValue ? Peer.PeerItemLookup.CleanIds(keys.SeriesProviderIds) : null;
        if (parentPath is null && !byName && ids is null && seriesIds is null)
        {
            return null;
        }

        return new PeerLookupRequest
        {
            Path = parentPath,
            ItemType = keys.ItemType,
            Name = byName ? keys.Name : null,
            ProviderIds = ids,
            SeriesProviderIds = seriesIds,
            IndexNumber = keys.IndexNumber,
            ParentIndexNumber = keys.ParentIndexNumber
        };
    }

    /// <summary>Finds an item on one source, trying the source it was pinned to first.</summary>
    /// <param name="keys">What identifies the item.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The match, which the caller disposes, or null.</returns>
    public async Task<PeerMatch?> FindAsync(PeerItemKeys keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (Pinned(keys.ProviderIds) is { } pinned)
        {
            var parents = await _parents.ParentsAsync(cancellationToken).ConfigureAwait(false);
            if (parents.FirstOrDefault(p => string.Equals(p.Key, pinned.ServerKey, StringComparison.OrdinalIgnoreCase)) is { } parent)
            {
                return new PeerMatch(parent, _clientFactory.Create(parent), pinned.ItemId);
            }
        }

        return (await FindAllAsync(keys, firstOnly: true, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>Finds an item on the sources.</summary>
    /// <param name="keys">What identifies the item.</param>
    /// <param name="firstOnly"><c>true</c> to stop at the first source that has it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The matches, which the caller disposes.</returns>
    public async Task<List<PeerMatch>> FindAllAsync(PeerItemKeys keys, bool firstOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var matches = new List<PeerMatch>();
        if (Plugin.Held)
        {
            return matches;
        }

        var parents = await _parents.ParentsAsync(cancellationToken).ConfigureAwait(false);
        if (parents.Count == 0)
        {
            _logger.LogInformation("Server Sync has no match for {Item}: no server this one pulls from answers as Server Sync {Version}", FormatUtilities.SanitizeForLog(keys.Describe()), _parents.Version);
            return matches;
        }

        var misses = new List<string>();
        foreach (var parent in parents)
        {
            if (LookupFor(parent, keys) is not { } request)
            {
                misses.Add($"'{parent.DisplayName}' maps no library holding it and it has no ids to match by");
                continue;
            }

            SourceServerClient? client = null;
            try
            {
                client = _clientFactory.Create(parent);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(LookupTimeout);
                var found = await client.LookupPeerItemAsync(request, deadline.Token).ConfigureAwait(false);
                if (found is { Found: true } && Guid.TryParse(found.ItemId, out var itemId))
                {
                    matches.Add(new PeerMatch(parent, client, itemId));
                    client = null;
                    if (firstOnly)
                    {
                        return matches;
                    }

                    continue;
                }

                misses.Add($"'{parent.DisplayName}' has no {keys.ItemType} at '{request.Path ?? "(no mapped path)"}' or with its ids");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var match in matches)
                {
                    match.Dispose();
                }

                throw;
            }
            // One source that cannot be asked must not hide the others.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                misses.Add($"'{parent.DisplayName}' could not be asked: {ex.Message}");
            }
            finally
            {
                client?.Dispose();
            }
        }

        if (matches.Count == 0)
        {
            _logger.LogInformation("Server Sync has no match for {Item}: {Reasons}", FormatUtilities.SanitizeForLog(keys.Describe()), FormatUtilities.SanitizeForLog(string.Join(". ", misses), 300 * misses.Count));
        }

        return matches;
    }

    /// <summary>Offers the items found on the sources as search results.</summary>
    /// <param name="itemType">The item's type name.</param>
    /// <param name="info">The lookup info that identifies the item.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>One result for each source that has the item.</returns>
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResultsAsync(string itemType, ItemLookupInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        var matches = await FindAllAsync(PeerItemKeys.FromInfo(itemType, info), firstOnly: false, cancellationToken).ConfigureAwait(false);
        var results = new List<RemoteSearchResult>();
        foreach (var match in matches)
        {
            using (match)
            {
                BaseItemDto? dto;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(LookupTimeout);
                    dto = (await match.Client.GetItemsByIdsAsync(new[] { match.ItemId }, Fields, cancellationToken: deadline.Token).ConfigureAwait(false)).FirstOrDefault();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Could not read {Item} from '{Parent}'", FormatUtilities.SanitizeForLog(info.Path ?? info.Name), FormatUtilities.SanitizeForLog(match.Parent.DisplayName));
                    continue;
                }

                if (dto is null)
                {
                    continue;
                }

                var ids = dto.ProviderIds?.AdditionalData?
                    .Where(kv => kv.Value is not null && !string.IsNullOrWhiteSpace(kv.Value.ToString()))
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()!, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ids[PinnedProviderKey] = PinValue(match.Parent, match.ItemId);
                results.Add(new RemoteSearchResult
                {
                    Name = $"{dto.Name} on {match.Parent.DisplayName}",
                    ProductionYear = dto.ProductionYear,
                    PremiereDate = dto.PremiereDate?.UtcDateTime,
                    IndexNumber = dto.IndexNumber,
                    ParentIndexNumber = dto.ParentIndexNumber,
                    Overview = dto.Overview,
                    ImageUrl = $"{match.Client.ServerUrl}/Items/{match.ItemId:N}/Images/Primary?maxHeight=400",
                    SearchProviderName = $"{ProviderName} ({match.Parent.DisplayName})",
                    ProviderIds = ids
                });
            }
        }

        return results;
    }

    /// <summary>Reads an item's metadata from the first source that has it.</summary>
    /// <typeparam name="TItem">The kind of item to fill.</typeparam>
    /// <param name="info">The lookup info that identifies the item.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The metadata, with <c>HasMetadata</c> <c>false</c> when no source has the item.</returns>
    public async Task<MetadataResult<TItem>> GetMetadataAsync<TItem>(ItemLookupInfo info, CancellationToken cancellationToken)
        where TItem : BaseItem, new()
    {
        ArgumentNullException.ThrowIfNull(info);
        var result = new MetadataResult<TItem> { HasMetadata = false, Provider = ProviderName };
        using var match = await FindAsync(PeerItemKeys.FromInfo(typeof(TItem).Name, info), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return result;
        }

        var pinned = Pinned(info.ProviderIds) is not null;

        BaseItemDto? dto;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(LookupTimeout);
            dto = (await match.Client.GetItemsByIdsAsync(new[] { match.ItemId }, Fields, cancellationToken: deadline.Token).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read {Item} from '{Parent}'", FormatUtilities.SanitizeForLog(info.Path ?? info.Name), FormatUtilities.SanitizeForLog(match.Parent.DisplayName));
            return result;
        }

        if (dto is null)
        {
            return result;
        }

        var blobs = new MetadataSyncItem();
        MetadataSyncMergeService.MergeMetadataFields(blobs, dto, null, syncGenres: true, syncTags: true);
        MetadataSyncMergeService.MergePeople(blobs, dto, null, _libraryManager);
        MetadataSyncMergeService.MergeStudios(blobs, dto, null);

        var item = new TItem();
        MetadataFieldWriter.Apply(item, blobs.Metadata.Source, syncGenres: true, syncTags: true, syncLocks: false);
        item.Studios = MetadataFieldWriter.ReadStudios(blobs.Studios.Source);
        if (pinned)
        {
            item.ProviderIds[PinnedProviderKey] = PinValue(match.Parent, match.ItemId);
        }

        result.Item = item;
        result.People = MetadataFieldWriter.ReadPeople(blobs.People.Source);
        result.ResultLanguage = dto.PreferredMetadataLanguage;
        result.HasMetadata = true;

        MarkFilled(info.Path, item is Person ? info.Name : null);
        _logger.LogDebug("Filled {Type} '{Item}' from '{Parent}'", typeof(TItem).Name, FormatUtilities.SanitizeForLog(info.Path ?? info.Name), FormatUtilities.SanitizeForLog(match.Parent.DisplayName));
        return result;
    }

    /// <summary>Lists the images the sources hold for an item.</summary>
    /// <param name="item">The local item to find images for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The images, with the pinned source's first.</returns>
    public async Task<IEnumerable<RemoteImageInfo>> GetImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var matches = await FindAllAsync(PeerItemKeys.FromItem(item), firstOnly: false, cancellationToken).ConfigureAwait(false);
        var pin = Pinned(item.ProviderIds);
        var ordered = matches
            .OrderBy(m => pin is { } p && string.Equals(m.Parent.Key, p.ServerKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        var found = new List<RemoteImageInfo>();
        foreach (var match in ordered)
        {
            using (match)
            {
                List<ImageInfo>? images;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(LookupTimeout);
                    images = await match.Client.GetItemImageInfoAsync(match.ItemId, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Could not read the images of {Item} from '{Parent}'", FormatUtilities.SanitizeForLog(item.Path ?? item.Name), FormatUtilities.SanitizeForLog(match.Parent.DisplayName));
                    continue;
                }

                var browserBase = string.IsNullOrWhiteSpace(match.Parent.BrowserUrl) ? match.Client.ServerUrl : match.Parent.BrowserUrl.TrimEnd('/');
                foreach (var image in images ?? new List<ImageInfo>())
                {
                    if (image.ImageType is null || !Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(image.ImageType.Value.ToString(), out var type))
                    {
                        continue;
                    }

                    found.Add(new RemoteImageInfo
                    {
                        ProviderName = $"{ProviderName} ({match.Parent.DisplayName})",
                        Type = type,
                        Url = $"{match.Client.ServerUrl}/Items/{match.ItemId:N}/Images/{type}/{image.ImageIndex ?? 0}",
                        ThumbnailUrl = $"{browserBase}/Items/{match.ItemId:N}/Images/{type}/{image.ImageIndex ?? 0}?maxHeight=400",
                        Width = image.Width,
                        Height = image.Height
                    });
                }
            }
        }

        if (found.Count > 0)
        {
            MarkFilled(item.Path, item is Person ? item.Name : null);
        }

        return found;
    }

    /// <summary>Fetches an image from a source.</summary>
    /// <param name="url">The image's address, which must be under a source's Server URL.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The response, which the caller disposes.</returns>
    public async Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        var parent = _parents.FindByUrl(url) ?? throw new HttpRequestException("The image address is not on a server this one pulls from");
        using var client = _clientFactory.Create(parent);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupTimeout);
        return await client.GetAuthorizedAsync(url, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Builds the value that pins an item to a source.</summary>
    /// <param name="parent">The source the item is pinned to.</param>
    /// <param name="itemId">The item's id on that server.</param>
    /// <returns>The value.</returns>
    public static string PinValue(SourceServer parent, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return parent.Key + ":" + itemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the source and item an item is pinned to from its provider ids.</summary>
    private static (string ServerKey, Guid ItemId)? Pinned(Dictionary<string, string>? ids)
    {
        if (ids is null || !ids.TryGetValue(PinnedProviderKey, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var split = value.IndexOf(':', StringComparison.Ordinal);
        return split > 0 && Guid.TryParse(value[(split + 1)..], out var itemId) ? (value[..split], itemId) : null;
    }

    /// <summary>Records that the provider filled an item, so its save is not announced as a local edit.</summary>
    private static void MarkFilled(string? path, string? personName)
    {
        ProviderFills.MarkPath(path);
        ProviderFills.MarkPerson(personName);
    }
}
