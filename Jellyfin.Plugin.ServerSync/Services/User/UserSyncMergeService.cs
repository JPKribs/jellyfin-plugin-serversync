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
    /// Serializes a value to JSON for storage.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <returns>JSON string or null.</returns>
    public static string? SerializeValue<T>(T? value)
    {
        if (value == null)
        {
            return null;
        }

        return JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// Deserializes a JSON value.
    /// </summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="json">JSON string.</param>
    /// <returns>Deserialized value or default.</returns>
    public static T? DeserializeValue<T>(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
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
    /// </summary>
    public static readonly HashSet<string> ExcludedPolicyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnabledChannels",        // Channel IDs differ
        "EnabledDevices",         // Device IDs are server-specific
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
    /// Checks if a policy property should be synced.
    /// </summary>
    public static bool ShouldSyncPolicyProperty(string propertyName)
    {
        return !ExcludedPolicyProperties.Contains(propertyName);
    }

    /// <summary>
    /// Checks if a configuration property should be synced.
    /// </summary>
    public static bool ShouldSyncConfigurationProperty(string propertyName)
    {
        return !ExcludedConfigurationProperties.Contains(propertyName);
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
    public static string? ExtractPolicyJson(object? policy, List<LibraryMapping>? libraryMappings = null)
    {
        if (policy == null) return null;

        var syncableProps = new Dictionary<string, object?>();
        var policyType = policy.GetType();

        foreach (var prop in policyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!ShouldSyncPolicyProperty(prop.Name)) continue;

            try
            {
                var value = prop.GetValue(policy);

                // Translate library IDs if needed
                if (libraryMappings != null && RequiresLibraryTranslation(prop.Name) && value is IEnumerable<string> ids)
                {
                    value = TranslateLibraryIds(ids.ToArray(), libraryMappings);
                }

                syncableProps[prop.Name] = value;
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
            if (!ShouldSyncConfigurationProperty(prop.Name)) continue;

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

    /// <summary>
    /// Compares two JSON strings for semantic equality.
    /// Delegates to the shared JsonComparisonUtility.
    /// </summary>
    /// <param name="json1">First JSON string.</param>
    /// <param name="json2">Second JSON string.</param>
    /// <returns>True if semantically equal, false otherwise.</returns>
    public static bool JsonEquals(string? json1, string? json2)
    {
        return Models.Common.JsonComparisonUtility.JsonEquals(json1, json2);
    }
}
