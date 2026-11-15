using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// Fills an item's metadata from a source.
/// </summary>
/// <typeparam name="TItem">The kind of item this provider fills.</typeparam>
/// <typeparam name="TInfo">The lookup info for that kind.</typeparam>
public abstract class PeerMetadataProvider<TItem, TInfo> : IRemoteMetadataProvider<TItem, TInfo>, IHasOrder
    where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
    where TInfo : ItemLookupInfo, new()
{
    private readonly PeerMetadataSource _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMetadataProvider{TItem, TInfo}"/> class.
    /// </summary>
    /// <param name="source">Finds items on the sources and reads them.</param>
    protected PeerMetadataProvider(PeerMetadataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public string Name => PeerMetadataSource.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public Task<MetadataResult<TItem>> GetMetadata(TInfo info, CancellationToken cancellationToken)
        => _source.GetMetadataAsync<TItem>(info, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo searchInfo, CancellationToken cancellationToken)
        => _source.GetSearchResultsAsync(typeof(TItem).Name, searchInfo, cancellationToken);

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _source.GetImageResponseAsync(url, cancellationToken);
}
