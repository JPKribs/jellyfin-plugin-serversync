using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.UserSync;

/// <summary>
/// Picks which user settings sync and builds the value to apply, where the source wins.
/// </summary>
public static class UserSyncMergeService
{
    /// <summary>
    /// Translates source library ids to local ones through the enabled library mappings, leaving out any with no mapping.
    /// </summary>
    /// <param name="sourceLibraryIds">The library ids as the source holds them, or null for none.</param>
    /// <param name="libraryMappings">The library mappings that pair each source library with a local one.</param>
    /// <returns>The local library ids.</returns>
    public static string[] TranslateLibraryIds(string[] sourceLibraryIds, List<LibraryMapping> libraryMappings)
    {
        ArgumentNullException.ThrowIfNull(libraryMappings);
        if (sourceLibraryIds == null || sourceLibraryIds.Length == 0)
        {
            return Array.Empty<string>();
        }

        var localIds = new List<string>();

        foreach (var mapping in sourceLibraryIds.Select(sourceId => libraryMappings.FirstOrDefault(m =>
            m.SourceLibraryId == sourceId &&
            m.IsEnabled &&
            !string.IsNullOrEmpty(m.LocalLibraryId))))
        {
            if (mapping != null)
            {
                localIds.Add(mapping.LocalLibraryId!);
            }
        }

        return localIds.ToArray();
    }

    /// <summary>
    /// The properties that hold library ids and need translating.
    /// </summary>
    public static readonly HashSet<string> LibraryIdProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnabledFolders",
        "EnableContentDeletionFromFolders"
    };

    /// <summary>
    /// The policy properties that never sync and that <see cref="IsGuidBearingType(Type)"/> cannot spot, such as ids held as strings and local state.
    /// The "all" switches stay local with the id lists they qualify, and administrator rights stay local so no server grants them on another.
    /// </summary>
    public static readonly HashSet<string> ExcludedPolicyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnabledChannels",
        "EnabledDevices",
        "EnableAllFolders",
        "EnableAllChannels",
        "EnableAllDevices",
        "EnableContentDeletionFromFolders",
        "IsAdministrator",
        "InvalidLoginAttemptCount",
        "AuthenticationProviderId",
        "PasswordResetProviderId"
    };

    /// <summary>
    /// The configuration properties that never sync because they hold ids or settings that belong to one server or device.
    /// </summary>
    public static readonly HashSet<string> ExcludedConfigurationProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "GroupedFolders",
        "OrderedViews",
        "LatestItemsExcludes",
        "MyMediaExcludes",
        "EnableLocalPassword",
        "CastReceiverId"
    };

    /// <summary>
    /// Determines whether a type holds a <see cref="Guid"/> anywhere inside it, since an id from one server names nothing on another.
    /// The check goes by type and not by name, so a Guid field Jellyfin adds later is left out without a change here.
    /// </summary>
    /// <param name="type">The property type to inspect.</param>
    /// <returns><c>true</c> when the type can carry an id that belongs to one server.</returns>
    public static bool IsGuidBearingType(Type type) => IsGuidBearingType(type, new HashSet<Type>());

    /// <summary>
    /// Walks a type for a <see cref="Guid"/>, visiting each type once so a type that refers to itself ends.
    /// </summary>
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

        // A collection is judged by what it holds, not by its own members.
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
    /// Determines whether a policy property syncs, by name alone, which misses a property that holds a Guid.
    /// </summary>
    /// <param name="propertyName">The property name to look up among the excluded ones.</param>
    /// <returns><c>true</c> when the property syncs.</returns>
    public static bool ShouldSyncPolicyProperty(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return !ExcludedPolicyProperties.Contains(propertyName);
    }

    /// <summary>
    /// Determines whether a configuration property syncs, by name alone, which misses a property that holds a Guid.
    /// </summary>
    /// <param name="propertyName">The property name to look up among the excluded ones.</param>
    /// <returns><c>true</c> when the property syncs.</returns>
    public static bool ShouldSyncConfigurationProperty(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return !ExcludedConfigurationProperties.Contains(propertyName);
    }

    /// <summary>
    /// Determines whether a policy property syncs, which needs a name that is not excluded and a type that holds no Guid.
    /// </summary>
    /// <param name="property">The policy property whose name and type are checked.</param>
    /// <returns><c>true</c> when the property syncs.</returns>
    public static bool ShouldSyncPolicyProperty(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ShouldSyncPolicyProperty(property.Name) && !IsGuidBearingType(property.PropertyType);
    }

    /// <summary>
    /// Determines whether a configuration property syncs, which needs a name that is not excluded and a type that holds no Guid.
    /// </summary>
    /// <param name="property">The configuration property whose name and type are checked.</param>
    /// <returns><c>true</c> when the property syncs.</returns>
    public static bool ShouldSyncConfigurationProperty(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ShouldSyncConfigurationProperty(property.Name) && !IsGuidBearingType(property.PropertyType);
    }

    /// <summary>
    /// Determines whether a property holds library ids that need translating.
    /// </summary>
    /// <param name="propertyName">The property name to look up.</param>
    /// <returns><c>true</c> when the property holds library ids.</returns>
    public static bool RequiresLibraryTranslation(string propertyName)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        return LibraryIdProperties.Contains(propertyName);
    }

    /// <summary>
    /// Reads the policy properties that sync into JSON.
    /// </summary>
    /// <param name="policy">The policy object to read, or null.</param>
    /// <returns>The JSON, or null when there is no policy.</returns>
    public static string? ExtractPolicyJson(object? policy)
    {
        if (policy == null) return null;

        var syncableProps = new Dictionary<string, object?>();
        var policyType = policy.GetType();

        foreach (var prop in policyType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(ShouldSyncPolicyProperty))
        {
            try
            {
                syncableProps[prop.Name] = prop.GetValue(policy);
            }

            // A property whose getter throws is left out.
            catch (TargetInvocationException)
            {
            }
        }

        return JsonSerializer.Serialize(syncableProps);
    }

    /// <summary>
    /// Reads the configuration properties that sync into JSON.
    /// </summary>
    /// <param name="config">The configuration object to read, or null.</param>
    /// <returns>The JSON, or null when there is no configuration.</returns>
    public static string? ExtractConfigurationJson(object? config)
    {
        if (config == null) return null;

        var syncableProps = new Dictionary<string, object?>();
        var configType = config.GetType();

        foreach (var prop in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(ShouldSyncConfigurationProperty))
        {
            try
            {
                syncableProps[prop.Name] = prop.GetValue(config);
            }

            // A property whose getter throws is left out.
            catch (TargetInvocationException)
            {
            }
        }

        return JsonSerializer.Serialize(syncableProps);
    }

    /// <summary>
    /// Builds the policy to apply from the source's, with its library ids translated to local ones.
    /// </summary>
    /// <param name="sourcePolicy">The source's policy JSON, or empty.</param>
    /// <param name="libraryMappings">The library mappings that translate library ids.</param>
    /// <returns>The merged JSON, the source policy unchanged when it cannot be read, or null when it is empty.</returns>
    public static string? ComputeMergedPolicy(string? sourcePolicy, List<LibraryMapping> libraryMappings)
    {
        ArgumentNullException.ThrowIfNull(libraryMappings);

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
