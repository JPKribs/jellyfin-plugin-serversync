using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Why a file is in the link cache.</summary>
public enum CacheReason
{
    /// <summary>Someone played the link.</summary>
    Played = 0,

    /// <summary>It is an episode ahead of the one a user is watching.</summary>
    Ahead = 1,

    /// <summary>It was a kept copy, moved here when it went back to being a link.</summary>
    Unkept = 2
}

/// <summary>One file in the link cache.</summary>
public sealed class CacheEntry
{
    /// <summary>Gets or sets the link's id.</summary>
    public string LinkId { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's name inside the cache.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when the file was cached.</summary>
    public DateTime CachedAt { get; set; }

    /// <summary>Gets or sets when the link was last played, if it has been since it was cached.</summary>
    public DateTime? LastPlayedAt { get; set; }

    /// <summary>Gets or sets why the file was cached.</summary>
    public CacheReason Reason { get; set; }
}

/// <summary>
/// The link cache: full local copies of the files behind some of this server's links, kept in one
/// folder under one size limit, wherever the links stream from. A link's .strm never changes. While
/// its file is here it plays from here under this server's own rules, and when it is not it streams
/// from the source. So a file can come and go without Jellyfin or a player noticing.
/// </summary>
/// <remarks>
/// The index sits in the folder with the files, so the folder is the whole cache: emptying it empties
/// the cache, and pointing the setting somewhere else starts a new one.
/// </remarks>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkCache
{
    /// <summary>The folder under Jellyfin's cache path the cache uses unless another is set.</summary>
    public const string DefaultFolder = "serversync-links";

    /// <summary>How a file being downloaded is named until it is whole.</summary>
    public const string PartExtension = ".part";

    // A cached file on its way into the library, beside where its copy goes.
    private const string MovePartExtension = ".serversync-move";

    private const string IndexFile = "index.json";

    private readonly IPluginConfigurationManager _configManager;
    private readonly ILibraryManager? _libraryManager;
    private volatile LibraryRoots? _libraryRoots;
    private bool _warnedInsideLibrary;
    private readonly string _defaultRoot;
    private readonly ILogger<LinkCache> _logger;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private Dictionary<string, CacheEntry>? _entries;
    private string? _loadedRoot;
    private int _loadedAtWipe;
    private bool _indexUnreadable;

    // Counts every folder emptied, by any caller, so an index held in memory is read again afterwards.
    private static int _wipes;

    public LinkCache(IPluginConfigurationManager configManager, IApplicationPaths paths, ILogger<LinkCache> logger, ILibraryManager? libraryManager = null)
        : this(configManager, Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).CachePath, DefaultFolder), logger, () => DateTime.UtcNow)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>Initializes a new instance of the <see cref="LinkCache"/> class, for tests.</summary>
    public LinkCache(IPluginConfigurationManager configManager, string defaultRoot, ILogger<LinkCache> logger, Func<DateTime> now)
    {
        _configManager = configManager;
        _defaultRoot = defaultRoot;
        _logger = logger;
        _now = now;
    }

    /// <summary>Gets a value indicating whether the cache is on.</summary>
    public bool Enabled => _configManager.Configuration.LinkCacheOn;

    /// <summary>Gets or sets how the free space on a folder's disk is read. Tests set it.</summary>
    public Func<string, long> FreeSpace { get; set; } = DiskFreeSpace;

    /// <summary>
    /// The most disk the cache may use now, in bytes, by its size setting. A cache sized by the room
    /// on its disk grows and shrinks with that disk, so this is read whenever room is needed.
    /// </summary>
    /// <returns>The limit, or 0 when the cache is off.</returns>
    public long LimitBytes()
    {
        var config = _configManager.Configuration;
        return config.EffectiveLinkCacheSizing switch
        {
            LinkCacheSizing.Off => 0,
            LinkCacheSizing.Fixed => config.GetLinkCacheLimitBytes(0, 0),
            _ => config.GetLinkCacheLimitBytes(UsedBytes() + DownloadingBytes(), FreeSpace(Root))
        };
    }

    // What the cache's own downloads in progress hold on the disk. They have already come out of the
    // free space and are not yet counted as cached, so without them the limit would dip while a file
    // downloads and come back when it finishes. With them, nothing the cache does moves its own limit:
    // a byte it takes leaves the free space and joins what it holds, and the two together stay the same.
    private long DownloadingBytes()
    {
        try
        {
            var root = Root;
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*" + PartExtension).Sum(part => new FileInfo(part).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // The free space on the disk a folder is on, or on its nearest parent that exists. 0 when it
    // cannot be read, so a cache sized by its disk takes nothing new until it can.
    private static long DiskFreeSpace(string folder)
    {
        try
        {
            var path = Path.GetFullPath(folder);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
            {
                path = parent;
            }

            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Gets the folder the cache keeps its files in.</summary>
    public string Root
    {
        get
        {
            var root = RootFor(_configManager.Configuration.LinkCachePath, _defaultRoot);
            return root == _defaultRoot || !InsideALibrary(root) ? root : _defaultRoot;
        }
    }

    // A cache folder inside a library would have its files scanned in as items, and a peer could then
    // stream or copy them like any file here. They are another server's files, so such a folder is not
    // used and the cache stays under Jellyfin's own cache. The library folders are read at most once a
    // minute.
    private bool InsideALibrary(string root)
    {
        if (_libraryManager is null)
        {
            return false;
        }

        var now = _now();
        var known = _libraryRoots;
        if (known is null || now - known.ReadAt > TimeSpan.FromMinutes(1))
        {
            try
            {
                var roots = _libraryManager.GetVirtualFolders()
                    .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
                    .Where(location => !string.IsNullOrWhiteSpace(location))
                    .ToList();
                known = new LibraryRoots(roots, now);
                _libraryRoots = known;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Could not read the library folders to check the cache location");
                return false;
            }
        }

        if (PathContainment.IsUnderAny(root, known.Roots))
        {
            if (!_warnedInsideLibrary)
            {
                _warnedInsideLibrary = true;
                _logger.LogWarning("The link cache location {Root} is inside a library, so the cache uses {Default} instead. Cached files are another server's and must not become items here", root, _defaultRoot);
            }

            return true;
        }

        return false;
    }

    private sealed record LibraryRoots(List<string> Roots, DateTime ReadAt);

    /// <summary>
    /// The folder a cache uses for a location setting. A location the administrator chose gets a folder
    /// of the cache's own inside it, so the cache never shares a folder with anything else and never
    /// removes a file that is not its own.
    /// </summary>
    /// <param name="location">The Cache Location setting, or empty.</param>
    /// <param name="defaultRoot">The folder under Jellyfin's cache.</param>
    /// <returns>The folder.</returns>
    public static string RootFor(string? location, string defaultRoot)
        => string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location) ? defaultRoot : Path.Combine(location, DefaultFolder);

    /// <summary>Gets the folder used when no other is set.</summary>
    public string DefaultRoot => _defaultRoot;

    /// <summary>The whole file for a link, when the cache is on and holds it, or null.</summary>
    public string? PathFor(string linkId)
    {
        if (!Enabled || string.IsNullOrEmpty(linkId))
        {
            return null;
        }

        lock (_lock)
        {
            if (!Entries().TryGetValue(linkId, out var entry))
            {
                return null;
            }

            var path = Path.Combine(Root, entry.File);
            try
            {
                if (new FileInfo(path) is { Exists: true } file && file.Length == entry.Size)
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            // Removed or cut short behind the cache's back: it is forgotten, and may be cached again.
            // A file left at the wrong size is the cache's own and plays as nothing, so it goes too.
            Delete(path);
            Entries().Remove(linkId);
            Save();
            return null;
        }
    }

    /// <summary>Whether the cache holds a link's file.</summary>
    public bool Holds(string linkId) => PathFor(linkId) is not null;

    /// <summary>Records that a cached link was played now.</summary>
    public void Played(string linkId)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_lock)
        {
            // A player reports often, and once a few minutes is enough to order the cache by.
            if (Entries().TryGetValue(linkId, out var entry) && (entry.LastPlayedAt is not { } last || _now() - last > TimeSpan.FromMinutes(5)))
            {
                entry.LastPlayedAt = _now();
                Save();
            }
        }
    }

    /// <summary>Where a link's file is written while it downloads. The folder is made if needed.</summary>
    public string PartPathFor(string linkId, string extension)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, FileNameFor(linkId, extension) + PartExtension);
    }

    /// <summary>
    /// Takes a finished download into the cache. False when the file is not the size it should be, in
    /// which case it is deleted.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="extension">The source file's extension.</param>
    /// <param name="expectedSize">The size the source reports for the file, or 0 when unknown.</param>
    /// <param name="reason">Why it was cached.</param>
    /// <returns>True when the file is now in the cache.</returns>
    public bool Complete(string linkId, string extension, long expectedSize, CacheReason reason)
    {
        var name = FileNameFor(linkId, extension);
        var part = Path.Combine(Root, name + PartExtension);
        var path = Path.Combine(Root, name);
        lock (_lock)
        {
            try
            {
                var size = new FileInfo(part).Length;
                if (size == 0 || (expectedSize > 0 && size != expectedSize))
                {
                    File.Delete(part);
                    return false;
                }

                File.Move(part, path, overwrite: true);
                Entries()[linkId] = new CacheEntry { LinkId = linkId, File = name, Size = size, CachedAt = _now(), Reason = reason };
                Save();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not take {File} into the link cache: {Reason}", name, ex.Message);
                return false;
            }
        }
    }

    /// <summary>Whether the cache holds a link's whole file, at <paramref name="size"/>.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="size">The size the source reports for the file now.</param>
    /// <returns>True when the cached file is whole and that size.</returns>
    public bool HoldsWhole(string linkId, long size)
    {
        lock (_lock)
        {
            return PathFor(linkId) is not null && Entries()[linkId].Size == size;
        }
    }

    /// <summary>
    /// Moves a link's cached file out of the cache to <paramref name="destination"/>, where it becomes
    /// the link's copy. The cache forgets the file first, under its lock, and the move runs outside it,
    /// so a move that is a copy between two disks never holds up playback. A move that fails is undone
    /// and the file is cached again. False, with nothing moved, when the cache does not hold the whole
    /// file at <paramref name="expectedSize"/>, or something is already at the destination.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="destination">Where the copy belongs.</param>
    /// <param name="expectedSize">The size the source reports for the file.</param>
    /// <returns>True when the file is now at the destination.</returns>
    public bool MoveTo(string linkId, string destination, long expectedSize)
    {
        CacheEntry entry;
        string cached;
        lock (_lock)
        {
            if (!HoldsWhole(linkId, expectedSize) || File.Exists(destination))
            {
                return false;
            }

            cached = PathFor(linkId)!;
            Entries().Remove(linkId, out entry!);
            Save();
        }

        // Moved under a name of its own beside the copy first, which between two disks is a copy, then
        // renamed into place, so a failure only ever leaves this move's own file to clear.
        var part = destination + MovePartExtension;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(cached, part, overwrite: true);
            File.Move(part, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not move {File} out of the link cache: {Reason}", Path.GetFileName(cached), ex.Message);
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(cached) && File.Exists(part))
                    {
                        File.Move(part, cached);
                    }
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Could not return {File} to the link cache: {Reason}", Path.GetFileName(cached), back.Message);
                }

                Delete(part);
                if (File.Exists(cached))
                {
                    Entries()[linkId] = entry;
                    Save();
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Takes a kept copy into the cache when it goes back to being a link, so it still plays from this
    /// server until the cache needs the room. False, with the file left where it was, when the cache is
    /// off, the file is larger than the whole cache, room cannot be made, or the move fails.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="file">The copy.</param>
    /// <param name="size">The size the source reports for the file.</param>
    /// <returns>True when the file is now in the cache.</returns>
    public bool Adopt(string linkId, string file, long size)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (!Enabled || size <= 0 || size > LimitBytes()
            || !File.Exists(file) || new FileInfo(file).Length != size
            || !MakeRoom(size, new HashSet<string>(StringComparer.Ordinal) { linkId }))
        {
            return false;
        }

        // Moved under the cache's download name first, which between two disks is a copy, so a move cut
        // short is cleared as an unfinished download and the copy is still where it was.
        var extension = Path.GetExtension(file);
        var part = PartPathFor(linkId, extension);
        try
        {
            File.Move(file, part, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not move {File} into the link cache: {Reason}", Path.GetFileName(file), ex.Message);
            if (File.Exists(file))
            {
                Delete(part);
            }

            return false;
        }

        return Complete(linkId, extension, size, CacheReason.Unkept);
    }

    /// <summary>Drops a link's file from the cache.</summary>
    public void Remove(string linkId)
    {
        lock (_lock)
        {
            if (Entries().Remove(linkId, out var entry))
            {
                Delete(Path.Combine(Root, entry.File));
                Save();
            }
        }
    }

    /// <summary>The files in the cache.</summary>
    public IReadOnlyList<CacheEntry> List()
    {
        lock (_lock)
        {
            return Entries().Values.ToList();
        }
    }

    /// <summary>The disk the cache uses, in bytes.</summary>
    public long UsedBytes()
    {
        lock (_lock)
        {
            return Entries().Values.Sum(e => e.Size);
        }
    }

    /// <summary>The order files are dropped in when room is needed, by the setting.</summary>
    public static IEnumerable<CacheEntry> RemovalOrder(IEnumerable<CacheEntry> entries, CacheRemovalOrder order)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return order switch
        {
            CacheRemovalOrder.Oldest => entries.OrderBy(e => e.CachedAt),
            CacheRemovalOrder.Newest => entries.OrderByDescending(e => e.CachedAt),
            CacheRemovalOrder.Largest => entries.OrderByDescending(e => e.Size).ThenBy(e => e.LastPlayedAt ?? e.CachedAt),
            _ => entries.OrderBy(e => e.LastPlayedAt ?? e.CachedAt)
        };
    }

    /// <summary>
    /// Drops what retention says is too old, then drops files in the removal order until the cache,
    /// with <paramref name="incoming"/> more bytes, fits its size. False when it still does not fit,
    /// because what is left may not be dropped.
    /// </summary>
    /// <param name="incoming">The size of a file about to be cached, or 0.</param>
    /// <param name="keep">Links that must not be dropped now, such as one being played.</param>
    /// <returns>True when there is room.</returns>
    public bool MakeRoom(long incoming, IReadOnlySet<string>? keep = null)
    {
        var config = _configManager.Configuration;
        lock (_lock)
        {
            // Read before anything is dropped. Dropping a file frees what it used, so the room on the
            // disk, and with it a limit taken from that room, stays the same through the loop.
            var limit = LimitBytes();
            var entries = Entries();
            var changed = false;
            if (config.LinkCacheRetentionDays > 0)
            {
                var cutoff = _now() - TimeSpan.FromDays(config.LinkCacheRetentionDays);
                foreach (var old in entries.Values.Where(e => (e.LastPlayedAt ?? e.CachedAt) < cutoff && keep?.Contains(e.LinkId) != true).ToList())
                {
                    _logger.LogInformation("Dropped {File} from the link cache: not played for {Days} day(s)", old.File, config.LinkCacheRetentionDays);
                    entries.Remove(old.LinkId);
                    Delete(Path.Combine(Root, old.File));
                    changed = true;
                }
            }

            var used = entries.Values.Sum(e => e.Size);
            foreach (var next in RemovalOrder(entries.Values, config.LinkCacheRemovalOrder).Where(e => keep?.Contains(e.LinkId) != true).ToList())
            {
                if (used + incoming <= limit)
                {
                    break;
                }

                _logger.LogInformation("Dropped {File} from the link cache to make room", next.File);
                entries.Remove(next.LinkId);
                Delete(Path.Combine(Root, next.File));
                used -= next.Size;
                changed = true;
            }

            if (changed)
            {
                Save();
            }

            return used + incoming <= limit;
        }
    }

    /// <summary>
    /// Clears what a stopped server left behind: downloads cut short, cache files the index does not
    /// list, and index entries whose file is gone. Whole cached files are kept, and so is anything in
    /// the folder that is not the cache's.
    /// </summary>
    public void Tidy()
    {
        lock (_lock)
        {
            var root = Root;
            if (!Directory.Exists(root))
            {
                return;
            }

            var entries = Entries();

            // With an index that could not be read, every file would look unlisted. They are left for
            // a start where the index reads.
            if (_indexUnreadable)
            {
                return;
            }

            var known = entries.Values.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root))
                {
                    var name = Path.GetFileName(file);
                    // Only files named the way this cache names them are its to remove.
                    if (OwnName(name) && !known.Contains(name))
                    {
                        Delete(file);
                    }
                }

                foreach (var gone in entries.Values.Where(e => !File.Exists(Path.Combine(root, e.File))).ToList())
                {
                    entries.Remove(gone.LinkId);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not tidy the link cache at {Root}: {Reason}", root, ex.Message);
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Empties a cache folder: every cached file, download, and the index. Only files this cache names
    /// are removed, so a folder shared with anything else keeps the rest.
    /// </summary>
    /// <param name="root">The folder.</param>
    public static void WipeFolder(string root)
    {
        Interlocked.Increment(ref _wipes);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, IndexFile, StringComparison.OrdinalIgnoreCase) || OwnName(name))
            {
                File.Delete(file);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    /// <summary>Empties the cache folder in use.</summary>
    public void Wipe()
    {
        lock (_lock)
        {
            try
            {
                WipeFolder(Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not empty the link cache at {Root}: {Reason}", Root, ex.Message);
            }

            _entries = null;
            _loadedRoot = null;
        }
    }

    // A cached file is named by its link's id, which is 32 hex characters, so files this cache did not
    // write are never taken for its own.
    private static string FileNameFor(string linkId, string extension)
    {
        if (linkId.Length != 32 || !linkId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A link id is 32 hex characters", nameof(linkId));
        }

        var clean = new string((extension ?? string.Empty).Where(char.IsLetterOrDigit).Take(8).ToArray());
        return clean.Length == 0 ? linkId.ToLowerInvariant() : linkId.ToLowerInvariant() + "." + clean.ToLowerInvariant();
    }

    private static bool OwnName(string name)
    {
        var stem = name.Split('.')[0];
        return stem.Length == 32 && stem.All(Uri.IsHexDigit);
    }

    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next tidy, such as a file a player still has open on Windows.
            _logger.LogDebug("Could not delete {Path} from the link cache: {Reason}", path, ex.Message);
        }
    }

    // The index of the folder in use, read again when the setting points somewhere else.
    private Dictionary<string, CacheEntry> Entries()
    {
        var root = Root;
        var wipes = Volatile.Read(ref _wipes);
        if (_entries is not null && string.Equals(root, _loadedRoot, StringComparison.Ordinal) && wipes == _loadedAtWipe)
        {
            return _entries;
        }

        _indexUnreadable = false;
        var entries = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var index = Path.Combine(root, IndexFile);
            if (File.Exists(index))
            {
                foreach (var entry in JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(index)) ?? new List<CacheEntry>())
                {
                    if (!string.IsNullOrEmpty(entry.LinkId) && OwnName(entry.File) && !entry.File.Contains('/', StringComparison.Ordinal) && !entry.File.Contains('\\', StringComparison.Ordinal))
                    {
                        entries[entry.LinkId] = entry;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            // The index is there and is not an index: cut short by a power loss, most likely. It will
            // not read on a later start either, so the cache starts over. The files it listed are
            // cleared as unlisted and cached again as they are played.
            entries.Clear();
            _logger.LogWarning("The link cache's index at {Root} is damaged, so the cache starts over: {Reason}", root, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _indexUnreadable = true;
            _logger.LogWarning("Could not read the link cache's index at {Root}, so it starts empty: {Reason}", root, ex.Message);
        }

        _entries = entries;
        _loadedRoot = root;
        _loadedAtWipe = wipes;
        return entries;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_loadedRoot!);
            var index = Path.Combine(_loadedRoot!, IndexFile);
            var temp = index + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries!.Values.ToList()));
            File.Move(temp, index, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save the link cache's index at {Root}: {Reason}", _loadedRoot, ex.Message);
        }
    }
}
