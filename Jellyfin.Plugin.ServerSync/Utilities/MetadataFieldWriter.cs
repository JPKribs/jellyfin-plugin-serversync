using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Utilities;

public static class MetadataFieldWriter
{
    public static bool Apply(MediaBrowser.Controller.Entities.BaseItem localItem, string? metadataJson, bool syncGenres, bool syncTags, bool syncLocks = true)

    {
        ArgumentNullException.ThrowIfNull(localItem);
        if (string.IsNullOrEmpty(metadataJson))
        {
            return false;
        }

        var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
        if (metadata == null)
        {
            throw new InvalidOperationException("Metadata source blob deserialized to null");
        }

        var hasChanges = false;
        var localVideo = localItem as MediaBrowser.Controller.Entities.Video;

        // Strings, assign through nulls and empty strings so source clearing
        // a field actually clears local.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Name", v => { if (!string.IsNullOrEmpty(v) && localItem.Name != v) { localItem.Name = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OriginalTitle", v => { if (localItem.OriginalTitle != v) { localItem.OriginalTitle = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Overview", v => { if (localItem.Overview != v) { localItem.Overview = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "OfficialRating", v => { if (localItem.OfficialRating != v) { localItem.OfficialRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "Tagline", v => { if (localItem.Tagline != v) { localItem.Tagline = v; return true; } return false; });
        // SortName intentionally not synced: Jellyfin derives SortName
        // from Name independently on each server, so writing it from
        // source produces a value that local will overwrite on next
        // metadata refresh anyway. ForcedSortName (user override) IS synced.
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "ForcedSortName", v => { if (localItem.ForcedSortName != v) { localItem.ForcedSortName = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "CustomRating", v => { if (localItem.CustomRating != v) { localItem.CustomRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "PreferredMetadataCountryCode", v => { if (localItem.PreferredMetadataCountryCode != v) { localItem.PreferredMetadataCountryCode = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignString(metadata, "PreferredMetadataLanguage", v => { if (localItem.PreferredMetadataLanguage != v) { localItem.PreferredMetadataLanguage = v; return true; } return false; });

        // Floats, number or null. Treat absent as no-op, present-and-null as
        // clear-local.
        hasChanges |= JsonFieldHelpers.AssignFloat(metadata, "CommunityRating", v => { if (localItem.CommunityRating != v) { localItem.CommunityRating = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignFloat(metadata, "CriticRating", v => { if (localItem.CriticRating != v) { localItem.CriticRating = v; return true; } return false; });

        // Ints, same semantic.
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "ProductionYear", v => { if (localItem.ProductionYear != v) { localItem.ProductionYear = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "IndexNumber", v => { if (localItem.IndexNumber != v) { localItem.IndexNumber = v; return true; } return false; });
        hasChanges |= JsonFieldHelpers.AssignInt(metadata, "ParentIndexNumber", v => { if (localItem.ParentIndexNumber != v) { localItem.ParentIndexNumber = v; return true; } return false; });

        // Dates, date-only compare.
        if (metadata.TryGetValue("PremiereDate", out var premiereValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(premiereValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localItem.PremiereDate, d))
            {
                localItem.PremiereDate = d;
                hasChanges = true;
            }
        }

        if (metadata.TryGetValue("EndDate", out var endValue))
        {
            var d = JsonFieldHelpers.ParseNullableDate(endValue);
            if (!JsonComparisonUtility.DateOnlyEquals(localItem.EndDate, d))
            {
                localItem.EndDate = d;
                hasChanges = true;
            }
        }

        // Arrays, empty/null source clears local.
        if (syncGenres && metadata.TryGetValue("Genres", out var genresValue))
        {
            var newGenres = JsonFieldHelpers.ReadStringArray(genresValue);
            if (!ArraysEqualOrdinal(localItem.Genres, newGenres))
            {
                localItem.Genres = newGenres;
                hasChanges = true;
            }
        }

        if (syncTags && metadata.TryGetValue("Tags", out var tagsValue))
        {
            var newTags = JsonFieldHelpers.ReadStringArray(tagsValue);
            if (!ArraysEqualOrdinal(localItem.Tags, newTags))
            {
                localItem.Tags = newTags;
                hasChanges = true;
            }
        }

        // ProviderIds: reconcile so local matches source exactly. Includes
        // removing local-only keys.
        if (metadata.TryGetValue("ProviderIds", out var providerIdsValue))
        {
            var sourceIds = JsonFieldHelpers.ReadProviderIds(providerIdsValue);
            // Remove local-only entries first.
            if (localItem.ProviderIds != null)
            {
                var toRemove = localItem.ProviderIds.Keys
                    .Where(k => !sourceIds.ContainsKey(k))
                    .ToList();
                foreach (var key in toRemove)
                {
                    localItem.ProviderIds.Remove(key);
                    hasChanges = true;
                }
            }

            foreach (var kvp in sourceIds)
            {
                if (localItem.GetProviderId(kvp.Key) != kvp.Value)
                {
                    localItem.SetProviderId(kvp.Key, kvp.Value);
                    hasChanges = true;
                }
            }
        }

        // Video-specific fields. Settable only when local is a Video.
        if (localVideo != null)
        {
            hasChanges |= JsonFieldHelpers.AssignString(metadata, "AspectRatio", v => { if (localVideo.AspectRatio != v) { localVideo.AspectRatio = v; return true; } return false; });

            if (metadata.TryGetValue("Video3DFormat", out var video3DValue))
            {
                Video3DFormat? format = null;
                if (video3DValue.ValueKind == JsonValueKind.String)
                {
                    var s = video3DValue.GetString();
                    if (!string.IsNullOrEmpty(s) && Enum.TryParse<Video3DFormat>(s, out var parsed))
                    {
                        format = parsed;
                    }
                }

                if (localVideo.Video3DFormat != format)
                {
                    localVideo.Video3DFormat = format;
                    hasChanges = true;
                }
            }
        }

        // LockedFields, empty source clears local.
        if (syncLocks && metadata.TryGetValue("LockedFields", out var lockedValue))
        {
            var newLocked = JsonFieldHelpers.ReadEnumArray<MetadataField>(lockedValue);
            if (!ArraysEqual(localItem.LockedFields, newLocked))
            {
                localItem.LockedFields = newLocked;
                hasChanges = true;
            }
        }

        // LockData, bool? coalesced to false when null, matching source-side
        // serialization (which writes `LockData ?? false`).
        if (syncLocks && metadata.TryGetValue("LockData", out var lockDataValue))
        {
            bool target = lockDataValue.ValueKind == JsonValueKind.True;
            if (localItem.IsLocked != target)
            {
                localItem.IsLocked = target;
                hasChanges = true;
            }
        }

        return hasChanges;
    }

    public static List<MediaBrowser.Controller.Entities.PersonInfo> ReadPeople(string? peopleJson)
    {
        var people = new List<MediaBrowser.Controller.Entities.PersonInfo>();
        if (string.IsNullOrEmpty(peopleJson))
        {
            return people;
        }

        foreach (var person in JsonSerializer.Deserialize<List<Dictionary<string, string>>>(peopleJson) ?? new List<Dictionary<string, string>>())
        {
            var personInfo = new MediaBrowser.Controller.Entities.PersonInfo();
            if (person.TryGetValue("Name", out var name))
            {
                personInfo.Name = name;
            }

            if (person.TryGetValue("Role", out var role))
            {
                personInfo.Role = role;
            }

            if (person.TryGetValue("Type", out var type) && Enum.TryParse<Jellyfin.Data.Enums.PersonKind>(type, out var personKind))
            {
                personInfo.Type = personKind;
            }

            if (!string.IsNullOrEmpty(personInfo.Name))
            {
                personInfo.SortOrder = people.Count;
                people.Add(personInfo);
            }
        }

        return people;
    }

    public static string[] ReadStudios(string? studiosJson)
        => string.IsNullOrEmpty(studiosJson) ? Array.Empty<string>() : (JsonSerializer.Deserialize<List<string>>(studiosJson) ?? new List<string>()).ToArray();

    private static bool ArraysEqualOrdinal(string[]? a, string[]? b)
    {
        var aArr = a ?? Array.Empty<string>();
        var bArr = b ?? Array.Empty<string>();
        if (aArr.Length != bArr.Length) return false;
        for (var i = 0; i < aArr.Length; i++)
        {
            if (!string.Equals(aArr[i], bArr[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static bool ArraysEqual<T>(T[]? a, T[]? b) where T : struct
    {
        var aArr = a ?? Array.Empty<T>();
        var bArr = b ?? Array.Empty<T>();
        if (aArr.Length != bArr.Length) return false;
        for (var i = 0; i < aArr.Length; i++)
        {
            if (!aArr[i].Equals(bArr[i])) return false;
        }

        return true;
    }
}
