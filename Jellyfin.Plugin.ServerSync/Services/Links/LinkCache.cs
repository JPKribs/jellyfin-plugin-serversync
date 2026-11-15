using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// The link cache, full local copies of the files behind some of this server's links, kept with their
/// index in one folder under one size limit. A link's .strm never changes, so its file can come and go,
/// playing from here under this server's own rules while held and streaming from the source when not.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkCache
{
    /// <summary>The folder under Jellyfin's cache path the cache uses unless another is set.</summary>
    public const string DefaultFolder = "serversync-links";

    /// <summary>How a file being downloaded is named until it is whole.</summary>
    public const string PartExtension = ".part";

    /// <summary>How a cached file is named on its way into the library, beside where its copy goes.</summary>
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

    /// <summary>Counts every folder emptied, by any caller, so an index held in memory is read again afterwards.</summary>
    private static int _wipes;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkCache"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="paths">Gives Jellyfin's cache folder, where the default cache folder sits.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Lists the library folders the cache must stay out of, or null to skip that check.</param>
    public LinkCache(IPluginConfigurationManager configManager, IApplicationPaths paths, ILogger<LinkCache> logger, ILibraryManager? libraryManager = null)
        : this(configManager, Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).CachePath, DefaultFolder), logger, () => DateTime.UtcNow)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkCache"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="defaultRoot">The cache folder used when none is set.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="now">Reads the current time, so a test can move it.</param>
    public LinkCache(IPluginConfigurationManager configManager, string defaultRoot, ILogger<LinkCache> logger, Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(defaultRoot);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(now);
        _configManager = configManager;
        _defaultRoot = defaultRoot;
        _logger = logger;
        _now = now;
    }

    /// <summary>Gets a value indicating whether the cache is on.</summary>
    public bool Enabled => _configManager.Configuration.LinkCacheOn;

    /// <summary>Gets or sets how the free space on a folder's disk is read.</summary>
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

    /// <summary>
    /// What the cache's own downloads in progress hold on the disk. Counting them keeps the limit steady while
    /// a file downloads, since those bytes have left the free space and are not yet counted as cached.
    /// </summary>
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

    /// <summary>
    /// The free space on the disk a folder is on, or on its nearest parent that exists. It is 0 when it
    /// cannot be read, so a cache sized by its disk takes nothing new until it can.
    /// </summary>
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

    /// <summary>
    /// Whether a cache folder is inside a library, where its files would be scanned in as items another
    /// server could then stream or copy. The library folders are read at most once a minute.
    /// </summary>
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

    /// <summary>The library folders, and when they were read.</summary>
    private sealed record LibraryRoots(List<string> Roots, DateTime ReadAt);

    /// <summary>
    /// The folder a cache uses for a location setting. A chosen location gets a folder of the cache's own
    /// inside it, so the cache never shares a folder and never removes a file that is not its own.
    /// </summary>
    /// <param name="location">The Cache Location setting, or empty.</param>
    /// <param name="defaultRoot">The folder under Jellyfin's cache.</param>
    /// <returns>The folder.</returns>
    public static string RootFor(string? location, string defaultRoot)
        => string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location) ? defaultRoot : Path.Combine(location, DefaultFolder);

    /// <summary>Gets the folder used when no other is set.</summary>
    public string DefaultRoot => _defaultRoot;

    /// <summary>The whole file for a link, when the cache is on and holds it, or null.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <returns>The file's path, or null.</returns>
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

            // A file removed or cut short behind the cache's back is forgotten.
            Delete(path);
            Entries().Remove(linkId);
            Save();
            return null;
        }
    }

    /// <summary>Whether the cache holds a link's file.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <returns><c>true</c> when the cache holds the file.</returns>
    public bool Holds(string linkId) => PathFor(linkId) is not null;

    /// <summary>Records that a cached link was played now.</summary>
    /// <param name="linkId">The link's id.</param>
    public void Played(string linkId)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_lock)
        {
            // A player reports often, and once every few minutes is enough.
            if (Entries().TryGetValue(linkId, out var entry) && (entry.LastPlayedAt is not { } last || _now() - last > TimeSpan.FromMinutes(5)))
            {
                entry.LastPlayedAt = _now();
                Save();
            }
        }
    }

    /// <summary>Where a link's file is written while it downloads. The folder is made if needed.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="extension">The file's extension, with its dot.</param>
    /// <returns>The path of the partial file.</returns>
    public string PartPathFor(string linkId, string extension)
    {
        ArgumentNullException.ThrowIfNull(linkId);
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, FileNameFor(linkId, extension) + PartExtension);
    }

    /// <summary>
    /// Takes a finished download into the cache, or deletes it and returns <c>false</c> when the file is
    /// not the size it should be.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="extension">The source file's extension.</param>
    /// <param name="expectedSize">The size the source reports for the file, or 0 when unknown.</param>
    /// <param name="reason">Why it was cached.</param>
    /// <returns><c>true</c> when the file is now in the cache.</returns>
    public bool Complete(string linkId, string extension, long expectedSize, CacheReason reason)
    {
        ArgumentNullException.ThrowIfNull(linkId);
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
                _logger.LogWarning("Could not take {File} into the link cache: {Reason}", name, FormatUtilities.SanitizeForLog(ex.Message, 300));
                return false;
            }
        }
    }

    /// <summary>Whether the cache holds a link's whole file, at <paramref name="size"/>.</summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="size">The size the source reports for the file now.</param>
    /// <returns><c>true</c> when the cached file is whole and that size.</returns>
    public bool HoldsWhole(string linkId, long size)
    {
        lock (_lock)
        {
            return PathFor(linkId) is not null && Entries()[linkId].Size == size;
        }
    }

    /// <summary>
    /// Moves a link's cached file to <paramref name="destination"/>, where it becomes the link's copy, or
    /// returns <c>false</c> when the cache does not hold the whole file or something is already there. The
    /// move runs outside the lock so a copy between two disks never holds up playback, and a failed move is undone.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="destination">Where the copy belongs.</param>
    /// <param name="expectedSize">The size the source reports for the file.</param>
    /// <returns><c>true</c> when the file is now at the destination.</returns>
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

        var part = destination + MovePartExtension;

        // Moving under its own name first leaves only this move's file to clear.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(cached, part, overwrite: true);
            File.Move(part, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not move {File} out of the link cache: {Reason}", Path.GetFileName(cached), FormatUtilities.SanitizeForLog(ex.Message, 300));
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
                    _logger.LogWarning("Could not return {File} to the link cache: {Reason}", Path.GetFileName(cached), FormatUtilities.SanitizeForLog(back.Message, 300));
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
    /// Takes a permanent copy into the cache when it goes back to being a link, so it still plays from this
    /// server until the cache needs the room. Returns <c>false</c>, with the file left where it was, when the
    /// cache is off, the file is larger than the whole cache, room cannot be made, or the move fails.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="file">The path of the permanent copy to take in.</param>
    /// <param name="size">The size the source reports for the file.</param>
    /// <returns><c>true</c> when the file is now in the cache.</returns>
    public bool Adopt(string linkId, string file, long size)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (!Enabled || size <= 0 || size > LimitBytes()
            || !File.Exists(file) || new FileInfo(file).Length != size
            || !MakeRoom(size, new HashSet<string>(StringComparer.Ordinal) { linkId }))
        {
            return false;
        }

        var extension = Path.GetExtension(file);
        var part = PartPathFor(linkId, extension);

        // A move cut short under the download name is cleared as an unfinished download.
        try
        {
            File.Move(file, part, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not move {File} into the link cache: {Reason}", FormatUtilities.SanitizeForLog(Path.GetFileName(file)), FormatUtilities.SanitizeForLog(ex.Message, 300));
            if (File.Exists(file))
            {
                Delete(part);
            }

            return false;
        }

        return Complete(linkId, extension, size, CacheReason.Unkept);
    }

    /// <summary>Drops a link's file from the cache.</summary>
    /// <param name="linkId">The link's id.</param>
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
    /// <returns>The cached files.</returns>
    public IReadOnlyList<CacheEntry> List()
    {
        lock (_lock)
        {
            return Entries().Values.ToList();
        }
    }

    /// <summary>The disk the cache uses, in bytes.</summary>
    /// <returns>The bytes used.</returns>
    public long UsedBytes()
    {
        lock (_lock)
        {
            return Entries().Values.Sum(e => e.Size);
        }
    }

    /// <summary>The order files are dropped in when room is needed, by the setting.</summary>
    /// <param name="entries">The cached files.</param>
    /// <param name="order">The setting that says which go first.</param>
    /// <returns>The files, first to drop first.</returns>
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
    /// with <paramref name="incoming"/> more bytes, fits its size. Returns <c>false</c> when it still does
    /// not fit, because what is left may not be dropped.
    /// </summary>
    /// <param name="incoming">The size of a file about to be cached, or 0.</param>
    /// <param name="keep">Links that must not be dropped now, such as one being played.</param>
    /// <returns><c>true</c> when there is room.</returns>
    public bool MakeRoom(long incoming, IReadOnlySet<string>? keep = null)
    {
        var config = _configManager.Configuration;
        lock (_lock)
        {
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

            // With an unreadable index every file would look unlisted, so all are left.
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
                _logger.LogWarning("Could not tidy the link cache at {Root}: {Reason}", root, FormatUtilities.SanitizeForLog(ex.Message, 300));
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Empties a cache folder of every cached file, download, and the index. Only files this cache names
    /// are removed, so a folder shared with anything else keeps the rest.
    /// </summary>
    /// <param name="root">The cache folder to empty.</param>
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
                _logger.LogWarning("Could not empty the link cache at {Root}: {Reason}", Root, FormatUtilities.SanitizeForLog(ex.Message, 300));
            }

            _entries = null;
            _loadedRoot = null;
        }
    }

    /// <summary>
    /// Names a cached file by its link's id, which is 32 hex characters, so files this cache did not
    /// write are never taken for its own.
    /// </summary>
    private static string FileNameFor(string linkId, string extension)
    {
        if (linkId.Length != 32 || !linkId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A link id is 32 hex characters", nameof(linkId));
        }

        var clean = new string((extension ?? string.Empty).Where(char.IsLetterOrDigit).Take(8).ToArray());
        return clean.Length == 0 ? linkId.ToLowerInvariant() : linkId.ToLowerInvariant() + "." + clean.ToLowerInvariant();
    }

    /// <summary>Whether a file is named the way this cache names its files.</summary>
    private static bool OwnName(string name)
    {
        var stem = name.Split('.')[0];
        return stem.Length == 32 && stem.All(Uri.IsHexDigit);
    }

    /// <summary>Deletes one of the cache's files, leaving it for the next tidy when it cannot be deleted.</summary>
    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next tidy, as when a player still has the file open.
            _logger.LogDebug("Could not delete {Path} from the link cache: {Reason}", path, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
    }

    /// <summary>The index of the folder in use, read again when the setting points elsewhere or a folder was emptied.</summary>
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
            // A damaged index will not read later either, so the cache starts over.
            entries.Clear();
            _logger.LogWarning("The link cache's index at {Root} is damaged, so the cache starts over: {Reason}", root, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _indexUnreadable = true;
            _logger.LogWarning("Could not read the link cache's index at {Root}, so it starts empty: {Reason}", root, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }

        _entries = entries;
        _loadedRoot = root;
        _loadedAtWipe = wipes;
        return entries;
    }

    /// <summary>Writes the index beside the files, replacing it only once the new one is whole.</summary>
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
            _logger.LogWarning("Could not save the link cache's index at {Root}: {Reason}", _loadedRoot, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
    }
}
