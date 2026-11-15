using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Offers the images the sources hold for an item.</summary>
public sealed class PeerImageProvider : IRemoteImageProvider, IHasOrder
{
    private static readonly ImageType[] Supported =
    {
        ImageType.Primary,
        ImageType.Art,
        ImageType.Backdrop,
        ImageType.Banner,
        ImageType.Logo,
        ImageType.Thumb,
        ImageType.Disc,
        ImageType.Box,
        ImageType.BoxRear,
        ImageType.Menu,
        ImageType.Screenshot
    };

    private readonly PeerMetadataSource _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerImageProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items on the sources and reads their images.</param>
    public PeerImageProvider(PeerMetadataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public string Name => PeerMetadataSource.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode or MusicAlbum or MusicArtist or Audio or AudioBook
        or MusicVideo or BoxSet or Person or Book or Trailer or Photo or PhotoAlbum or Video;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => Supported;

    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        => _source.GetImagesAsync(item, cancellationToken);

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _source.GetImageResponseAsync(url, cancellationToken);
}
