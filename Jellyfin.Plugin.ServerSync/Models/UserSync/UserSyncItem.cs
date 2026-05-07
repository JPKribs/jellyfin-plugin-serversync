using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Property categories for user sync items.
/// </summary>
public static class UserPropertyCategory
{
    /// <summary>
    /// User policy settings (permissions, restrictions).
    /// </summary>
    public const string Policy = "Policy";

    /// <summary>
    /// User configuration settings (preferences).
    /// </summary>
    public const string Configuration = "Configuration";

    /// <summary>
    /// User profile image.
    /// </summary>
    public const string ProfileImage = "ProfileImage";
}

/// <summary>
/// Represents a single property sync record for a user mapping.
/// One record per property category (Policy, Configuration, ProfileImage)
/// per user mapping. Three categories means up to three rows per user.
/// </summary>
public class UserSyncItem : SyncRecord
{
    // ===== User Mapping =====

    /// <summary>
    /// Gets or sets the source server user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server user ID.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source user's display name.
    /// </summary>
    public string? SourceUserName { get; set; }

    /// <summary>
    /// Gets or sets the local user's display name.
    /// </summary>
    public string? LocalUserName { get; set; }

    // ===== Property Info =====

    /// <summary>
    /// Gets or sets the property category (Policy, Configuration, ProfileImage).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    // ===== Values (Policy/Configuration use these; ProfileImage stores display strings here) =====

    /// <summary>
    /// Gets or sets the source value (JSON for Policy/Config, display string for ProfileImage).
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Gets or sets the local value.
    /// </summary>
    public string? LocalValue { get; set; }

    /// <summary>
    /// Gets or sets the merged value (what will be applied).
    /// </summary>
    public string? MergedValue { get; set; }

    /// <summary>
    /// Gets or sets the SHA fingerprint of <see cref="SourceValue"/>. Reserved
    /// for future fast-path comparisons; currently unused but maintained in the
    /// schema so a later refactor can drop in <see cref="SyncableValue{T}"/>.
    /// </summary>
    public string? SourceValueHash { get; set; }

    /// <summary>
    /// Gets or sets the SHA fingerprint of the source value at the last
    /// successful sync.
    /// </summary>
    public string? SyncedValueHash { get; set; }

    // ===== Profile Image specific =====

    /// <summary>
    /// Gets or sets the source profile image hash (SHA256, truncated).
    /// </summary>
    public string? SourceImageHash { get; set; }

    /// <summary>
    /// Gets or sets the local profile image hash (SHA256, truncated).
    /// </summary>
    public string? LocalImageHash { get; set; }

    /// <summary>
    /// Gets or sets the last synced image hash (to detect if we already synced).
    /// </summary>
    public string? SyncedImageHash { get; set; }

    /// <summary>
    /// Gets or sets the source profile image size in bytes.
    /// </summary>
    public long? SourceImageSize { get; set; }

    /// <summary>
    /// Gets or sets the local profile image size in bytes.
    /// </summary>
    public long? LocalImageSize { get; set; }

    /// <summary>
    /// Gets or sets the last synced image size.
    /// </summary>
    public long? SyncedImageSize { get; set; }

    // ===== Computed change detection =====

    /// <inheritdoc />
    public override bool HasChanges
    {
        get
        {
            if (PropertyCategory == UserPropertyCategory.ProfileImage)
            {
                var sourceHasImage = !string.IsNullOrEmpty(SourceImageHash) || (SourceImageSize ?? 0) > 0;
                var localHasImage = !string.IsNullOrEmpty(LocalImageHash) || (LocalImageSize ?? 0) > 0;

                // Source removed its image but local still has one: queue the deletion.
                if (!sourceHasImage)
                {
                    return localHasImage;
                }

                if (!string.IsNullOrEmpty(SourceImageHash))
                {
                    return !string.Equals(SourceImageHash, LocalImageHash, StringComparison.OrdinalIgnoreCase);
                }

                return SourceImageSize != LocalImageSize;
            }

            // For Policy and Configuration: short-circuit when the merged
            // value's hash matches the last-synced hash — no apply needed.
            if (!string.IsNullOrEmpty(SourceValueHash)
                && string.Equals(SourceValueHash, SyncedValueHash, StringComparison.Ordinal))
            {
                return false;
            }

            return !JsonComparisonUtility.JsonEquals(MergedValue, LocalValue);
        }
    }

    /// <inheritdoc />
    public override void MarkSynced()
    {
        if (PropertyCategory == UserPropertyCategory.ProfileImage)
        {
            SyncedImageHash = SourceImageHash;
            SyncedImageSize = SourceImageSize;
        }
        else
        {
            SyncedValueHash = SourceValueHash;
        }
    }

    /// <summary>
    /// Gets a display-friendly summary of the change.
    /// </summary>
    public string ChangesSummary
    {
        get
        {
            if (!HasChanges)
            {
                return "No changes";
            }

            if (PropertyCategory == UserPropertyCategory.ProfileImage)
            {
                return SourceImageSize.HasValue ? FormatUtilities.FormatBytes(SourceImageSize.Value) : "None";
            }

            var diffCount = JsonComparisonUtility.CountDifferences(MergedValue, LocalValue);
            return diffCount switch
            {
                0 => "No changes",
                1 => "1 difference",
                _ => $"{diffCount} differences"
            };
        }
    }
}
