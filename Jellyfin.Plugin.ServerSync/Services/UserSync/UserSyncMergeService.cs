using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for merging user data using a source-wins strategy.
/// </summary>
public static class UserSyncMergeService
{
    /// <summary>
    /// Translates library IDs from source server to local server using library mappings.
    /// Used for EnabledFolders and EnableContentDeletionFromFolders properties.
    /// </summary>
    /// <param name="sourceLibraryIds">Array of source library IDs.</param>
    /// <param name="libraryMappings">Available library mappings.</param>
    /// <returns>Translated local library IDs.</returns>
    public static string[] TranslateLibraryIds(string[] sourceLibraryIds, List<LibraryMapping> libraryMappings)
    {
        if (sourceLibraryIds == null || sourceLibraryIds.Length == 0)
        {
            return Array.Empty<string>();
        }

        var localIds = new List<string>();

        foreach (var sourceId in sourceLibraryIds)
        {
            // Find mapping for this source library
            var mapping = libraryMappings.FirstOrDefault(m =>
                m.SourceLibraryId == sourceId &&
                m.IsEnabled &&
                !string.IsNullOrEmpty(m.LocalLibraryId));

            if (mapping != null)
            {
                localIds.Add(mapping.LocalLibraryId!);
            }
            // If no mapping found, skip this library (user won't have access)
        }

        return localIds.ToArray();
    }

    /// <summary>
    /// Properties that require library ID translation.
    /// </summary>
    public static readonly HashSet<string> LibraryIdProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnabledFolders",
        "EnableContentDeletionFromFolders"
    };

    /// <summary>
    /// Properties that should NOT be synced (server-specific).
    /// <para>
    /// Guid-typed identifiers are rejected by <see cref="IsGuidBearingType"/>
    /// and don't need naming here. This list exists for the ones reflection
    /// can't recognise: identifiers Jellyfin models as <c>string</c>, plus
    /// runtime state and local-only security settings.
    /// </para>
    /// </summary>
    public static readonly HashSet<string> ExcludedPolicyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnabledChannels",        // Channel IDs differ (also Guid-typed)
        "EnabledDevices",         // Device IDs are server-specific (string[])
        // The "all" switches only mean something next to the id lists above,
        // and those lists stay local. Copying EnableAllFolders=false onto a
        // user whose local EnabledFolders is empty locks them out of every
        // library, and copying true overrides a local restriction.
        "EnableAllFolders",
        "EnableAllChannels",
        "EnableAllDevices",
        // Library IDs held as strings, so the type walker can't see them.
        // Untranslated they name libraries that don't exist locally, and
        // translating them silently drops any library without a mapping , 
        // which revokes local deletion rights the operator never touched.
        "EnableContentDeletionFromFolders",
        "InvalidLoginAttemptCount", // Runtime state
        "AuthenticationProviderId", // Provider-specific
        "PasswordResetProviderId"   // Provider-specific
    };

    /// <summary>
    /// Properties that should NOT be synced from Configuration.
    /// </summary>
    public static readonly HashSet<string> ExcludedConfigurationProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "GroupedFolders",        // UI-specific, IDs differ
        "OrderedViews",          // UI-specific, IDs differ
        "LatestItemsExcludes",   // UI-specific, IDs differ
        "MyMediaExcludes",       // UI-specific, IDs differ
        "EnableLocalPassword",   // Security, local-only
        "CastReceiverId"         // Device-specific
    };

    /// <summary>
    /// True when a type carries a Jellyfin identifier: a <see cref="Guid"/>, a
    /// nullable Guid, a collection of either, or a complex type with a Guid
    /// anywhere inside it.
    /// <para>
    /// Identifiers are per-install. A source server's library, channel, or user
    /// GUID names nothing on the local server, so copying one into a local
    /// user's policy either silently grants no access or writes a dangling
    /// reference. <c>BlockedMediaFolders</c> and <c>BlockedChannels</c> are
    /// plain <c>Guid[]</c>, and <c>AccessSchedules</c> embeds the source user's
    /// own <c>UserId</c>, none of which a name-based blocklist caught.
    /// </para>
    /// <para>
    /// Driven by type rather than by name so a future Jellyfin release that
    /// adds a Guid-bearing field is excluded automatically instead of silently
    /// syncing until someone notices.
    /// </para>
    /// </summary>
    /// <param name="type">Property type to inspect.</param>
    /// <returns>True when the type can carry a server-specific identifier.</returns>
    public static bool IsGuidBearingType(Type type) => IsGuidBearingType(type, new HashSet<Type>());

    private static bool IsGuidBearingType(Type? type, HashSet<Type> visited)
    {
        if (type == null || !visited.Add(type))
        {
            return false;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(Guid))
        {
            return true;
        }

        // Leaf types can't contain anything.
        if (underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(TimeSpan))
        {
            return false;
        }

        // Collections: inspect what they hold, not the collection's own members.
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
        {
            if (underlying.IsArray)
            {
                return IsGuidBearingType(underlying.GetElementType(), visited);
            }

            foreach (var arg in underlying.GetGenericArguments())
            {
                if (IsGuidBearingType(arg, visited))
                {
                    return true;
                }
            }

            return false;
        }

        // Complex type, one Guid property anywhere makes the whole value unsafe
        // to copy across servers (AccessSchedule.UserId).
        foreach (var property in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsGuidBearingType(property.PropertyType, visited))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a policy property should be synced, by name only. Prefer the
    /// <see cref="PropertyInfo"/> overload, it also rejects Guid-bearing types.
    /// </summary>
    public static bool ShouldSyncPolicyProperty(string propertyName)
    {
        return !ExcludedPolicyProperties.Contains(propertyName);
    }

    /// <summary>
    /// Checks if a configuration property should be synced, by name only.
    /// Prefer the <see cref="PropertyInfo"/> overload.
    /// </summary>
    public static bool ShouldSyncConfigurationProperty(string propertyName)
    {
        return !ExcludedConfigurationProperties.Contains(propertyName);
    }

    /// <summary>
    /// Checks if a policy property should be synced: not on the name blocklist
    /// (which covers identifiers Jellyfin models as strings, so reflection
    /// can't see them) and not carrying a Guid.
    /// </summary>
    public static bool ShouldSyncPolicyProperty(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ShouldSyncPolicyProperty(property.Name) && !IsGuidBearingType(property.PropertyType);
    }

    /// <summary>
    /// Checks if a configuration property should be synced. See
    /// <see cref="ShouldSyncPolicyProperty(PropertyInfo)"/>.
    /// </summary>
    public static bool ShouldSyncConfigurationProperty(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ShouldSyncConfigurationProperty(property.Name) && !IsGuidBearingType(property.PropertyType);
    }

    /// <summary>
    /// Checks if a property requires library ID translation.
    /// </summary>
    public static bool RequiresLibraryTranslation(string propertyName)
    {
        return LibraryIdProperties.Contains(propertyName);
    }

    /// <summary>
    /// Extracts syncable policy properties from a policy object and returns as JSON.
    /// </summary>
    public static string? ExtractPolicyJson(object? policy)
    {
        if (policy == null) return null;

        var syncableProps = new Dictionary<string, object?>();
        var policyType = policy.GetType();

        foreach (var prop in policyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!ShouldSyncPolicyProperty(prop)) continue;

            try
            {
                // Library id fields are Guid typed and excluded above, so nothing read here needs
                // translating. ComputeMergedPolicy translates the merged value instead.
                syncableProps[prop.Name] = prop.GetValue(policy);
            }
            catch (TargetInvocationException)
            {
                // Skip properties that throw during read
            }
        }

        return JsonSerializer.Serialize(syncableProps);
    }

    /// <summary>
    /// Extracts syncable configuration properties from a config object and returns as JSON.
    /// </summary>
    public static string? ExtractConfigurationJson(object? config)
    {
        if (config == null) return null;

        var syncableProps = new Dictionary<string, object?>();
        var configType = config.GetType();

        foreach (var prop in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!ShouldSyncConfigurationProperty(prop)) continue;

            try
            {
                syncableProps[prop.Name] = prop.GetValue(config);
            }
            catch (TargetInvocationException)
            {
                // Skip properties that throw during read
            }
        }

        return JsonSerializer.Serialize(syncableProps);
    }

    /// <summary>
    /// Computes merged policy JSON (source-wins, with library ID translation).
    /// </summary>
    public static string? ComputeMergedPolicy(string? sourcePolicy, List<LibraryMapping> libraryMappings)
    {
        if (string.IsNullOrEmpty(sourcePolicy)) return null;

        try
        {
            var sourceDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sourcePolicy);
            if (sourceDict == null) return sourcePolicy;

            var mergedDict = new Dictionary<string, object?>();

            foreach (var kvp in sourceDict)
            {
                if (RequiresLibraryTranslation(kvp.Key) && kvp.Value.ValueKind == JsonValueKind.Array)
                {
                    var sourceIds = kvp.Value.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => s != null)
                        .ToArray();
                    mergedDict[kvp.Key] = TranslateLibraryIds(sourceIds!, libraryMappings);
                }
                else
                {
                    // Source-wins: use source value directly
                    mergedDict[kvp.Key] = kvp.Value;
                }
            }

            return JsonSerializer.Serialize(mergedDict);
        }
        catch (JsonException)
        {
            return sourcePolicy;
        }
    }
}
