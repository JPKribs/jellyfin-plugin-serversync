using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
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

public sealed record PeerMatch(SourceServer Parent, SourceServerClient Client, Guid ItemId) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerMetadataSource
{
    public const string ProviderName = "Server Sync";

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

    public PeerMetadataSource(ParentDirectory parents, ISourceServerClientFactory clientFactory, ILibraryManager libraryManager, ILogger<PeerMetadataSource> logger)
    {
        _parents = parents;
        _clientFactory = clientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

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

    public static PeerLookupRequest? LookupFor(SourceServer parent, string itemType, string? localPath, string? name)
    {
        var parentPath = ToParentPath(parent, localPath);
        var byName = ByName.Contains(itemType) && !string.IsNullOrWhiteSpace(name);
        if (parentPath is null && !byName)
        {
            return null;
        }

        return new PeerLookupRequest { Path = parentPath, ItemType = itemType, Name = byName ? name : null };
    }

    public async Task<PeerMatch?> FindAsync(string itemType, string? localPath, string? name, CancellationToken cancellationToken)
    {
        foreach (var parent in await _parents.ParentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (LookupFor(parent, itemType, localPath, name) is not { } request)
            {
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
                    var match = new PeerMatch(parent, client, itemId);
                    client = null;
                    return match;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not look up {Type} '{Item}' on '{Parent}'", itemType, localPath ?? name, parent.DisplayName);
            }
            finally
            {
                client?.Dispose();
            }
        }

        return null;
    }

    public async Task<MetadataResult<TItem>> GetMetadataAsync<TItem>(ItemLookupInfo info, CancellationToken cancellationToken)
        where TItem : BaseItem, new()
    {
        ArgumentNullException.ThrowIfNull(info);
        var result = new MetadataResult<TItem> { HasMetadata = false, Provider = ProviderName };
        using var match = await FindAsync(typeof(TItem).Name, info.Path, info.Name, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return result;
        }

        BaseItemDto? dto;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(LookupTimeout);
            dto = (await match.Client.GetItemsByIdsAsync(new[] { match.ItemId }, Fields, cancellationToken: deadline.Token).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read {Item} from '{Parent}'", info.Path ?? info.Name, match.Parent.DisplayName);
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
        result.Item = item;
        result.People = MetadataFieldWriter.ReadPeople(blobs.People.Source);
        result.ResultLanguage = dto.PreferredMetadataLanguage;
        result.HasMetadata = true;

        MarkFilled(info.Path, item is Person ? info.Name : null);
        _logger.LogDebug("Filled {Type} '{Item}' from '{Parent}'", typeof(TItem).Name, info.Path ?? info.Name, match.Parent.DisplayName);
        return result;
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var match = await FindAsync(item.GetType().Name, item.Path, item.Name, cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        List<ImageInfo>? images;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(LookupTimeout);
            images = await match.Client.GetItemImageInfoAsync(match.ItemId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the images of {Item} from '{Parent}'", item.Path ?? item.Name, match.Parent.DisplayName);
            return Array.Empty<RemoteImageInfo>();
        }

        var found = new List<RemoteImageInfo>();
        foreach (var image in images ?? new List<ImageInfo>())
        {
            if (image.ImageType is null || !Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(image.ImageType.Value.ToString(), out var type))
            {
                continue;
            }

            found.Add(new RemoteImageInfo
            {
                ProviderName = ProviderName,
                Type = type,
                Url = $"{match.Client.ServerUrl}/Items/{match.ItemId:N}/Images/{type}/{image.ImageIndex ?? 0}",
                Width = image.Width,
                Height = image.Height
            });
        }

        if (found.Count > 0)
        {
            MarkFilled(item.Path, item is Person ? item.Name : null);
        }

        return found;
    }

    public async Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        var parent = _parents.FindByUrl(url) ?? throw new HttpRequestException("The image address is not on a server this one pulls from");
        using var client = _clientFactory.Create(parent);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupTimeout);
        return await client.GetAuthorizedAsync(url, deadline.Token).ConfigureAwait(false);
    }

    private static void MarkFilled(string? path, string? personName)
    {
        ProviderFills.MarkPath(path);
        ProviderFills.MarkPerson(personName);
    }
}
