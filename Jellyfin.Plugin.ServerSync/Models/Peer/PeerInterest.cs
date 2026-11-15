using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// What this server wants to hear about from one peer, so the peer sends only hints that matter here.
/// </summary>
public class PeerInterest
{
    /// <summary>Gets or sets the kinds of hint this server accepts.</summary>
    public List<HintKind> Kinds { get; set; } = new();

    /// <summary>Gets or sets the mapped libraries, by the peer's root folder.</summary>
    public List<PeerInterestLibrary> Libraries { get; set; } = new();

    /// <summary>Gets or sets the mapped peer users, as 32 character hex ids.</summary>
    public List<string> Users { get; set; } = new();

    /// <summary>Gets or sets a hash of the three lists, so a peer can tell when they changed.</summary>
    public string Revision { get; set; } = string.Empty;

    /// <summary>Builds what this server wants from one peer, from the settings and that peer's entry.</summary>
    /// <param name="config">The plugin settings.</param>
    /// <param name="entry">The peer's server entry.</param>
    /// <returns>The interest, with its revision set.</returns>
    public static PeerInterest For(PluginConfiguration config, SourceServer entry)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(entry);
        var interest = new PeerInterest
        {
            Kinds = HintProtocol.AcceptedKinds(config).ToList(),
            Libraries = entry.GetEnabledLibraryMappings()
                .Where(m => !string.IsNullOrWhiteSpace(m.SourceRootPath))
                .Select(m => new PeerInterestLibrary
                {
                    Root = m.SourceRootPath,
                    FilterMode = m.FilterMode,
                    Filtered = m.FilterMode == LibraryFilterMode.AllowAll
                        ? new List<string>()
                        : (m.FilteredItems ?? new List<FilteredItem>()).Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).OrderBy(p => p, StringComparer.Ordinal).ToList()
                })
                .OrderBy(l => l.Root, StringComparer.Ordinal)
                .ToList(),
            Users = entry.GetEnabledUserMappings()
                .Select(m => Guid.TryParse(m.SourceUserId, out var id) ? id.ToString("N", CultureInfo.InvariantCulture) : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
        };
        interest.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { interest.Kinds, interest.Libraries, interest.Users }))))[..16];
        return interest;
    }

    /// <summary>Whether a change belongs to something this server wants to hear about.</summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="itemPath">The item's path on the peer, or null for a change with no item.</param>
    /// <param name="userId">The peer user the change belongs to.</param>
    /// <returns><c>true</c> when the peer should send it.</returns>
    public bool Wants(HintKind kind, string? itemPath, Guid userId)
    {
        if (!Kinds.Contains(kind))
        {
            return false;
        }

        if (kind == HintKind.People)
        {
            return true;
        }

        if (kind == HintKind.Playlists)
        {
            return Users.Contains(userId.ToString("N", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        }

        if (kind == HintKind.History && !Users.Contains(userId.ToString("N", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        var library = Libraries
            .Where(l => HintMapping.IsUnder(itemPath, l.Root))
            .OrderByDescending(l => l.Root.Length)
            .FirstOrDefault();
        if (library is null)
        {
            return false;
        }

        return library.FilterMode == LibraryFilterMode.AllowAll
            || library.Filtered.Count == 0
            || !PathUtilities.IsItemFiltered(itemPath, library.Root, library.FilterMode, library.Filtered.Select(p => new FilteredItem { Path = p }).ToList());
    }
}
