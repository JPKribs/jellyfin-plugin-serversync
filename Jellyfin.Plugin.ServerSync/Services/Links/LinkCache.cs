using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Why a file is in the link cache.</summary>
public enum CacheReason
{
    /// <summary>Someone played the link.</summary>
    Played = 0,

    /// <summary>It is an episode ahead of the one a user is watching.</summary>
    Ahead = 1
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

    private const string IndexFile = "index.json";

    private readonly IPluginConfigurationManager _configManager;
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

    public LinkCache(IPluginConfigurationManager configManager, IApplicationPaths paths, ILogger<LinkCache> logger)
        : this(configManager, Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).CachePath, DefaultFolder), logger, () => DateTime.UtcNow)
    {
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
    public bool Enabled => _configManager.Configuration.LinkCacheSizeGb > 0;

    /// <summary>Gets the folder the cache keeps its files in.</summary>
    public string Root => RootFor(_configManager.Configuration.LinkCachePath, _defaultRoot);

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

    /// <summary>
    /// Moves a link's cached file out of the cache to <paramref name="destination"/>, where it becomes
    /// the link's copy. False, with nothing moved, when the cache does not hold the whole file, the file
    /// is not <paramref name="expectedSize"/>, or something is already at the destination.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="destination">Where the copy belongs.</param>
    /// <param name="expectedSize">The size the source reports for the file.</param>
    /// <returns>True when the file is now at the destination.</returns>
    public bool MoveTo(string linkId, string destination, long expectedSize)
    {
        lock (_lock)
        {
            if (PathFor(linkId) is not { } cached || new FileInfo(cached).Length != expectedSize || File.Exists(destination))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(cached, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not move {File} out of the link cache: {Reason}", Path.GetFileName(cached), ex.Message);
                return false;
            }

            Entries().Remove(linkId);
            Save();
            return true;
        }
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
        var limit = config.GetLinkCacheSizeBytes();
        lock (_lock)
        {
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
        System.Threading.Interlocked.Increment(ref _wipes);
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
        var wipes = System.Threading.Volatile.Read(ref _wipes);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
