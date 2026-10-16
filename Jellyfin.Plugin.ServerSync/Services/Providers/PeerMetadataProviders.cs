using System.Collections.Generic;
using System.Linq;
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

public abstract class PeerMetadataProvider<TItem, TInfo> : IRemoteMetadataProvider<TItem, TInfo>, IHasOrder
    where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
    where TInfo : ItemLookupInfo, new()
{
    private readonly PeerMetadataSource _source;

    protected PeerMetadataProvider(PeerMetadataSource source)
    {
        _source = source;
    }

    public string Name => PeerMetadataSource.ProviderName;

    public int Order => 0;

    public Task<MetadataResult<TItem>> GetMetadata(TInfo info, CancellationToken cancellationToken)
        => _source.GetMetadataAsync<TItem>(info, cancellationToken);

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _source.GetImageResponseAsync(url, cancellationToken);
}

public sealed class PeerMovieProvider : PeerMetadataProvider<Movie, MovieInfo>
{
    public PeerMovieProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerSeriesProvider : PeerMetadataProvider<Series, SeriesInfo>
{
    public PeerSeriesProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerSeasonProvider : PeerMetadataProvider<Season, SeasonInfo>
{
    public PeerSeasonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerEpisodeProvider : PeerMetadataProvider<Episode, EpisodeInfo>
{
    public PeerEpisodeProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerAlbumProvider : PeerMetadataProvider<MusicAlbum, AlbumInfo>
{
    public PeerAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerArtistProvider : PeerMetadataProvider<MusicArtist, ArtistInfo>
{
    public PeerArtistProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerSongProvider : PeerMetadataProvider<Audio, SongInfo>
{
    public PeerSongProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerAudioBookProvider : PeerMetadataProvider<AudioBook, SongInfo>
{
    public PeerAudioBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerMusicVideoProvider : PeerMetadataProvider<MusicVideo, MusicVideoInfo>
{
    public PeerMusicVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerBoxSetProvider : PeerMetadataProvider<BoxSet, BoxSetInfo>
{
    public PeerBoxSetProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerPersonProvider : PeerMetadataProvider<Person, PersonLookupInfo>
{
    public PeerPersonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerBookProvider : PeerMetadataProvider<Book, BookInfo>
{
    public PeerBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerTrailerProvider : PeerMetadataProvider<Trailer, TrailerInfo>
{
    public PeerTrailerProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerVideoProvider : PeerMetadataProvider<Video, ItemLookupInfo>
{
    public PeerVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerPhotoProvider : PeerMetadataProvider<Photo, ItemLookupInfo>
{
    public PeerPhotoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

public sealed class PeerPhotoAlbumProvider : PeerMetadataProvider<PhotoAlbum, ItemLookupInfo>
{
    public PeerPhotoAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

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

    public PeerImageProvider(PeerMetadataSource source)
    {
        _source = source;
    }

    public string Name => PeerMetadataSource.ProviderName;

    public int Order => 0;

    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode or MusicAlbum or MusicArtist or Audio or AudioBook
        or MusicVideo or BoxSet or Person or Book or Trailer or Photo or PhotoAlbum or Video;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => Supported;

    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        => _source.GetImagesAsync(item, cancellationToken);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _source.GetImageResponseAsync(url, cancellationToken);
}
