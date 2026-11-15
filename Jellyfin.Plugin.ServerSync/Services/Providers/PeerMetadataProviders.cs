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

/// <summary>
/// Fills an item's metadata from the server this one pulls from.
/// </summary>
/// <typeparam name="TItem">The kind of item.</typeparam>
/// <typeparam name="TInfo">The lookup info for that kind.</typeparam>
public abstract class PeerMetadataProvider<TItem, TInfo> : IRemoteMetadataProvider<TItem, TInfo>, IHasOrder
    where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
    where TInfo : ItemLookupInfo, new()
{
    private readonly PeerMetadataSource _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMetadataProvider{TItem, TInfo}"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    protected PeerMetadataProvider(PeerMetadataSource source)
    {
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

/// <summary>Fills the metadata of movies from the server this one pulls from.</summary>
public sealed class PeerMovieProvider : PeerMetadataProvider<Movie, MovieInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMovieProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerMovieProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of series from the server this one pulls from.</summary>
public sealed class PeerSeriesProvider : PeerMetadataProvider<Series, SeriesInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSeriesProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSeriesProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of seasons from the server this one pulls from.</summary>
public sealed class PeerSeasonProvider : PeerMetadataProvider<Season, SeasonInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSeasonProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSeasonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of episodes from the server this one pulls from.</summary>
public sealed class PeerEpisodeProvider : PeerMetadataProvider<Episode, EpisodeInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerEpisodeProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerEpisodeProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of music albums from the server this one pulls from.</summary>
public sealed class PeerAlbumProvider : PeerMetadataProvider<MusicAlbum, AlbumInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAlbumProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of music artists from the server this one pulls from.</summary>
public sealed class PeerArtistProvider : PeerMetadataProvider<MusicArtist, ArtistInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerArtistProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerArtistProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of songs from the server this one pulls from.</summary>
public sealed class PeerSongProvider : PeerMetadataProvider<Audio, SongInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSongProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSongProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of audio books from the server this one pulls from.</summary>
public sealed class PeerAudioBookProvider : PeerMetadataProvider<AudioBook, SongInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAudioBookProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerAudioBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of music videos from the server this one pulls from.</summary>
public sealed class PeerMusicVideoProvider : PeerMetadataProvider<MusicVideo, MusicVideoInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMusicVideoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerMusicVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of collections from the server this one pulls from.</summary>
public sealed class PeerBoxSetProvider : PeerMetadataProvider<BoxSet, BoxSetInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerBoxSetProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerBoxSetProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of people from the server this one pulls from.</summary>
public sealed class PeerPersonProvider : PeerMetadataProvider<Person, PersonLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPersonProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPersonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of books from the server this one pulls from.</summary>
public sealed class PeerBookProvider : PeerMetadataProvider<Book, BookInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerBookProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of trailers from the server this one pulls from.</summary>
public sealed class PeerTrailerProvider : PeerMetadataProvider<Trailer, TrailerInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrailerProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerTrailerProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of videos from the server this one pulls from.</summary>
public sealed class PeerVideoProvider : PeerMetadataProvider<Video, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerVideoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of photos from the server this one pulls from.</summary>
public sealed class PeerPhotoProvider : PeerMetadataProvider<Photo, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPhotoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPhotoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Fills the metadata of photo albums from the server this one pulls from.</summary>
public sealed class PeerPhotoAlbumProvider : PeerMetadataProvider<PhotoAlbum, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPhotoAlbumProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPhotoAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

/// <summary>Offers the images of the server this one pulls from for an item.</summary>
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
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerImageProvider(PeerMetadataSource source)
    {
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

/// <summary>Shows which server and item an item was pinned to, as an external id in Jellyfin.</summary>
public sealed class PeerItemExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Server Sync Item";

    /// <inheritdoc />
    public string Key => PeerMetadataSource.PinnedProviderKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is not MediaBrowser.Controller.Playlists.Playlist;
}
