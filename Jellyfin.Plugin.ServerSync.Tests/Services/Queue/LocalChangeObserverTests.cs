using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The observer announces a file rewritten in place by its new file time, and holds a removal long enough for the new file of a rename to go first.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class LocalChangeObserverTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    private static Movie Film(DateTime fileTime, string path = "/media/Movies/Film (2020)/Film (2020).mkv")
        => new() { Id = Guid.NewGuid(), Name = "Film", Path = path, DateModified = fileTime };

    /// <summary>
    /// A file import is a rewrite when the file time is newer than the stamp of the file's last announcement, or recent when it was never announced.
    /// True: a file rewritten at the same path is announced once, and a first scan of an old library announces nothing.
    /// False: a rewrite never reaches other servers, or every refresh announces the whole library again.
    /// </summary>
    [Theory]
    [InlineData("newer than the stamp", true)]
    [InlineData("older than the stamp", false)]
    [InlineData("no stamp, recent", true)]
    [InlineData("no stamp, old", false)]
    [InlineData("not an import", false)]
    [InlineData("a .strm", false)]
    [InlineData("a folder", false)]
    public void IsRewrite_AnnouncesOnlyANewFileTime(string state, bool expected)
    {
        var fileTime = state.Contains("old", StringComparison.Ordinal) ? Now.AddDays(-30) : Now.AddMinutes(-5);
        BaseItem item = state switch
        {
            "a .strm" => Film(fileTime, "/media/Movies/Film/Film.strm"),
            "a folder" => new Folder { Id = Guid.NewGuid(), Path = "/media/Movies/Film", DateModified = fileTime },
            _ => Film(fileTime)
        };
        var stamp = state.StartsWith("no stamp", StringComparison.Ordinal) || state is "a .strm" or "a folder" or "not an import"
            ? null
            : new ObjectVersion { ServerId = "here", Timestamp = state == "newer than the stamp" ? Now.AddHours(-1) : Now };
        var reason = state == "not an import" ? ItemUpdateType.MetadataEdit : ItemUpdateType.MetadataImport;

        Assert.Equal(expected, LocalChangeObserver.IsRewrite(item, reason, stamp, Now));
    }

    /// <summary>
    /// A file that leaves the library waits out the removal hold before it is announced, and it shows in the queue as removed meanwhile.
    /// True: the new file of a rename or a new container reaches a receiver before the old file's removal.
    /// False: a receiver deletes its copy before it hears that the file was only renamed.
    /// </summary>
    [Fact]
    public async Task Removal_IsHeldThenAnnounced()
    {
        _harness.Config.Configuration.Servers.Add(QueueSamples.Server());
        var (_, observer) = _harness.Hints();
        await observer.StartAsync(CancellationToken.None);
        var film = Film(Now, QueueSamples.LocalPath("Film (2020)/Film (2020).mkv"));

        Raise("ItemRemoved", film);
        var held = Assert.Single(observer.Gathering());
        var before = observer.Flush(held.EditedAt + HintProtocol.RemovalHold - TimeSpan.FromSeconds(1));
        var after = observer.Flush(held.EditedAt + HintProtocol.RemovalHold);

        Assert.Equal((HintKind.Content, film.Id, film.Path), (held.Kind, held.ItemId, held.RemovedPath));
        Assert.Equal(held.EditedAt + HintProtocol.RemovalHold, held.Due);
        Assert.Equal((0, 1), (before, after));
        Assert.Empty(observer.Gathering());
        var sent = Assert.Single(_harness.Outbound.GetAll());
        Assert.Equal((HintKind.Content, HintProtocol.ContentRemoved, film.Path), (sent.Kind, sent.ItemType, sent.ItemPath));
        await observer.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A file back at the same path within the hold drops the held removal.
    /// True: a file replaced in place at the same name is never announced as removed.
    /// False: a receiver deletes its copy of a file that is still on the source.
    /// </summary>
    [Fact]
    public async Task Removal_FileBackWithinTheHold_IsDropped()
    {
        var (_, observer) = _harness.Hints();
        await observer.StartAsync(CancellationToken.None);
        var film = Film(Now);

        Raise("ItemRemoved", film);
        Raise("ItemAdded", film);

        Assert.DoesNotContain(observer.Gathering(), change => change.RemovedPath is not null);
        Assert.Equal(0, observer.Flush(DateTime.MaxValue));
        await observer.StopAsync(CancellationToken.None);
    }

    /// <summary>Raises one of the library's item events through the handler the observer subscribed.</summary>
    private void Raise(string eventName, BaseItem item)
    {
        var handler = (EventHandler<ItemChangeEventArgs>)_harness.LibraryStub.Calls.Last(c => c.Name == "add_" + eventName).Args[0]!;
        handler(null, new ItemChangeEventArgs { Item = item });
    }
}
