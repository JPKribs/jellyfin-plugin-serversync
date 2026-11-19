using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>
/// Answers Jellyfin's media segment scan with the segments the metadata scan read from a source for the same file, without asking the source.
/// It runs last, and replaces or removes the segments other providers gave a file only while Replace Existing Segments or Remove Segments No Server Has is on.
/// When it holds nothing for an item, it gives back the segments Jellyfin holds, so nothing is removed.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(IMediaSegmentProvider))]
public sealed class PeerSegmentProvider : IMediaSegmentProvider, IHasOrder
{
    /// <summary>How far the runtimes of two copies of one file may differ.</summary>
    public static readonly TimeSpan RunTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly IPluginConfigurationManager _configManager;
    private readonly MediaSegmentStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<PeerSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSegmentProvider"/> class.
    /// </summary>
    /// <param name="configManager">Reads whether the provider is on.</param>
    /// <param name="store">Holds the segments the metadata scan read from the sources.</param>
    /// <param name="libraryManager">Finds the local item Jellyfin asks about.</param>
    /// <param name="services">Finds Jellyfin's segment manager when first needed, since that manager is built from the providers.</param>
    /// <param name="logger">Writes why the segments of an item were not used.</param>
    public PeerSegmentProvider(IPluginConfigurationManager configManager, MediaSegmentStore store, ILibraryManager libraryManager, IServiceProvider services, ILogger<PeerSegmentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _configManager = configManager;
        _store = store;
        _libraryManager = libraryManager;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => PeerMetadataSource.ProviderName;

    /// <summary>
    /// Gets the place among the providers, last, so a provider that runs after it cannot add segments back to a file it answered for.
    /// A library that orders its providers itself decides instead.
    /// </summary>
    public int Order => int.MaxValue;

    /// <summary>
    /// Says whether Jellyfin should ask for an item's segments, which needs Enable Metadata Sync and Enable Media Segment Provider on.
    /// While either is off it says no, so the segments here stay as they are.
    /// </summary>
    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        var config = _configManager.Configuration;
        return ValueTask.FromResult(
            item is Video or MediaBrowser.Controller.Entities.Audio.Audio
            && !Plugin.Held
            && config.EnableMetadataSync
            && config.MetadataSyncSegmentProvider);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<MediaSegmentDto> kept = (request.ExistingSegments ?? Enumerable.Empty<MediaSegmentDto>()).ToList();
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return kept;
        }

        SourceMediaSegments? held;
        try
        {
            held = _store.Get(request.ItemId.ToString("N", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "Could not read the media segments kept for {Path}, so Jellyfin's are kept", item.Path);
            return kept;
        }

        if (held is null)
        {
            return kept;
        }

        // Segments fit one file, so another file at the same path takes none.
        if (!SameFile(item, held))
        {
            _logger.LogDebug("The source's file for {Path} differs in size or runtime, so its media segments are not used", item.Path);
            return Array.Empty<MediaSegmentDto>();
        }

        var fitted = Fit(held.Segments, request.ItemId, item.RunTimeTicks);
        if (_services.GetService(typeof(IMediaSegmentManager)) is not IMediaSegmentManager manager)
        {
            return fitted;
        }

        var others = (await manager.GetSegmentsAsync(item, null, OthersOnly(_libraryManager.GetLibraryOptions(item)), filterByProvider: true).ConfigureAwait(false)).ToList();
        var config = _configManager.Configuration;
        var plan = Plan(fitted, others, kept, config.MetadataSyncReplaceSegments, config.MetadataSyncRemoveSegments);
        if (plan.RemoveOthers)
        {
            foreach (var segment in others)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await manager.DeleteSegmentAsync(segment.Id).ConfigureAwait(false);
            }

            _logger.LogDebug("Removed {Count} media segments other providers gave {Path}", others.Count, item.Path);
        }

        return plan.Ours;
    }

    /// <summary>
    /// Decides what a file's segments become. The source's segments are added when the file has no others. Segments another provider gave it stay
    /// unless replacing is on, and nothing is added beside them. When no server has segments for the file, its segments stay unless removing is on.
    /// </summary>
    /// <param name="fitted">The source's segments, fitted to the file, empty when no server has any.</param>
    /// <param name="others">The segments other providers gave the file.</param>
    /// <param name="ours">The segments Server Sync gave the file before.</param>
    /// <param name="replace">Whether Replace Existing Segments is on.</param>
    /// <param name="remove">Whether Remove Segments No Server Has is on.</param>
    /// <returns>Server Sync's segments for the file, and whether the other providers' are removed.</returns>
    public static SegmentPlan Plan(IReadOnlyList<MediaSegmentDto> fitted, IReadOnlyList<MediaSegmentDto> others, IReadOnlyList<MediaSegmentDto> ours, bool replace, bool remove)
    {
        ArgumentNullException.ThrowIfNull(fitted);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(ours);
        if (fitted.Count == 0)
        {
            return remove ? new SegmentPlan(Array.Empty<MediaSegmentDto>(), others.Count > 0) : new SegmentPlan(ours, false);
        }

        if (others.Count == 0)
        {
            return new SegmentPlan(fitted, false);
        }

        // Another provider's segments stay when they already match the source's, or when replacing is off.
        if (!replace || SameSegments(others, fitted))
        {
            return new SegmentPlan(Array.Empty<MediaSegmentDto>(), false);
        }

        return new SegmentPlan(fitted, true);
    }

    /// <summary>
    /// Gives a library's settings with Server Sync's media segment provider turned off, so a read returns only the segments other providers made.
    /// </summary>
    /// <param name="options">The library's settings, or null.</param>
    /// <returns>The settings to read with.</returns>
    public static LibraryOptions OthersOnly(LibraryOptions? options) => new()
    {
        DisabledMediaSegmentProviders = (options?.DisabledMediaSegmentProviders ?? Array.Empty<string>()).Append(PeerMetadataSource.ProviderName).ToArray()
    };

    /// <summary>
    /// Says whether two lists hold the same segments by type and times, in any order.
    /// </summary>
    /// <param name="first">One list.</param>
    /// <param name="second">The other list.</param>
    /// <returns><c>true</c> when both hold the same segments.</returns>
    public static bool SameSegments(IReadOnlyCollection<MediaSegmentDto> first, IReadOnlyCollection<MediaSegmentDto> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        static IEnumerable<(MediaSegmentType, long, long)> Keys(IEnumerable<MediaSegmentDto> list)
            => list.Select(s => (s.Type, s.StartTicks, s.EndTicks)).Distinct().OrderBy(k => k);
        return Keys(first).SequenceEqual(Keys(second));
    }

    /// <summary>
    /// Does nothing, since the segments it keeps belong to the metadata scan, which forgets them when the item leaves the Metadata table.
    /// </summary>
    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Says whether the local item's file is the one the source's segments fit. Sizes must match when both are known and the local item is not a link,
    /// and runtimes must be within <see cref="RunTimeTolerance"/> when both are known.
    /// </summary>
    /// <param name="item">The local item.</param>
    /// <param name="held">The segments and the source file they fit.</param>
    /// <returns><c>true</c> when nothing known says the files differ.</returns>
    public static bool SameFile(BaseItem item, SourceMediaSegments held)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(held);
        if (!item.IsShortcut && item.Size is > 0 && held.Size is > 0 && item.Size != held.Size)
        {
            return false;
        }

        return item.RunTimeTicks is not > 0 || held.RunTimeTicks is not > 0
            || Math.Abs(item.RunTimeTicks.Value - held.RunTimeTicks.Value) <= RunTimeTolerance.Ticks;
    }

    /// <summary>
    /// Turns a source's segments into segments for the local item. A segment of an unknown type or a repeat of one already given is left out,
    /// and one that runs past the local file's end is cut at the end, or left out when it starts there.
    /// </summary>
    /// <param name="segments">The segments the source holds.</param>
    /// <param name="itemId">The local item they belong to.</param>
    /// <param name="runTimeTicks">How long the local file runs, or null when Jellyfin does not know.</param>
    /// <returns>The segments to store, each with a new id.</returns>
    public static List<MediaSegmentDto> Fit(IEnumerable<PeerSegment?> segments, Guid itemId, long? runTimeTicks)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var fitted = new List<MediaSegmentDto>();
        foreach (var segment in segments)
        {
            if (segment is null || !Enum.TryParse<MediaSegmentType>(segment.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            {
                continue;
            }

            var start = Math.Max(0, segment.StartTicks);
            var end = runTimeTicks is > 0 ? Math.Min(segment.EndTicks, runTimeTicks.Value) : segment.EndTicks;
            // Two providers on the source can mark the same part, and one copy of it is enough here.
            if (end <= start || fitted.Exists(f => f.Type == type && f.StartTicks == start && f.EndTicks == end))
            {
                continue;
            }

            fitted.Add(new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId, Type = type, StartTicks = start, EndTicks = end });
        }

        return fitted;
    }
}
