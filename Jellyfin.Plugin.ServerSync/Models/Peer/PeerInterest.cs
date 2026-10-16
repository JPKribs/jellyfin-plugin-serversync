using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

public class PeerInterest
{
    public List<HintKind> Kinds { get; set; } = new();

    public List<PeerInterestLibrary> Libraries { get; set; } = new();

    public List<string> Users { get; set; } = new();

    public string Revision { get; set; } = string.Empty;

    public static PeerInterest For(global::Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, SourceServer entry)
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
                .Select(m => Guid.TryParse(m.SourceUserId, out var id) ? id.ToString("N", System.Globalization.CultureInfo.InvariantCulture) : null)
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
        };
        interest.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { interest.Kinds, interest.Libraries, interest.Users }))))[..16];
        return interest;
    }

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

        if (kind == HintKind.History && !Users.Contains(userId.ToString("N", System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
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

public class PeerInterestLibrary
{
    public string Root { get; set; } = string.Empty;

    public LibraryFilterMode FilterMode { get; set; }

    public List<string> Filtered { get; set; } = new();
}
