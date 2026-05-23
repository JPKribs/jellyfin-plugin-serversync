using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
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
/// One property sync record per (user mapping, category). Policy and
/// Configuration use an internal <see cref="SyncableValue{T}"/> for change
/// detection; ProfileImage uses its own hash-pair since its "value" is the
/// image bytes' fingerprint.
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

    // ===== Values =====

    /// <summary>
    /// Raw source value (JSON for Policy/Config, display string for ProfileImage).
    /// Display-only — change detection uses <see cref="Value"/> for
    /// Policy/Config and the image-hash fields for ProfileImage.
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Local value extracted at refresh time. Delegated to
    /// <see cref="Value"/>'s <see cref="SyncableValue{T}.Local"/> so the
    /// comparator path on <see cref="SyncableValue{T}.HasChanges"/> works
    /// uniformly with the other modules.
    /// </summary>
    public string? LocalValue
    {
        get => Value.Local;
        set => Value.Local = value;
    }

    /// <summary>
    /// Computed apply target (source-wins with library-ID translation for
    /// Policy; identical to source for Configuration). Delegated to
    /// <see cref="Value"/>'s <see cref="SyncableValue{T}.Source"/>. Use
    /// <see cref="UpdateMergedValue"/> in the refresh build path so the
    /// source-hash gets recomputed via the comparator.
    /// </summary>
    public string? MergedValue
    {
        get => Value.Source;
        set => Value.Source = value;
    }

    /// <summary>
    /// SHA fingerprint of <see cref="MergedValue"/>. Delegated to
    /// <see cref="Value"/>'s <see cref="SyncableValue{T}.SourceHash"/>.
    /// </summary>
    public string? SourceValueHash
    {
        get => Value.SourceHash;
        set => Value.SourceHash = value;
    }

    /// <summary>
    /// Source-value hash captured at the last successful sync. Delegated to
    /// <see cref="Value"/>'s <see cref="SyncableValue{T}.SyncedHash"/>.
    /// </summary>
    public string? SyncedValueHash
    {
        get => Value.SyncedHash;
        set => Value.SyncedHash = value;
    }

    /// <summary>
    /// Internal <see cref="SyncableValue{T}"/> that drives change detection
    /// for Policy / Configuration categories. ProfileImage has its own
    /// hash-pair below.
    /// </summary>
    public SyncableValue<string> Value { get; } = new SyncableValue<string>
    {
        Comparator = new JsonBlobComparator()
    };

    /// <summary>
    /// Sets <see cref="MergedValue"/> and recomputes its hash via the
    /// comparator. Call from the refresh build path so
    /// <see cref="SyncableValue{T}.SourceHash"/> stays consistent with
    /// the format the rest of the SyncableValue plumbing produces.
    /// </summary>
    public void UpdateMergedValue(string? mergedValue) => Value.UpdateSource(mergedValue);

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

            // Policy / Configuration: the SyncableValue handles both the
            // hash short-circuit and the comparator fallback.
            return Value.HasChanges;
        }
    }

    /// <inheritdoc />
    public override void MarkSynced()
    {
        if (PropertyCategory == UserPropertyCategory.ProfileImage)
        {
            SyncedImageHash = SourceImageHash;
            SyncedImageSize = SourceImageSize;
            return;
        }

        Value.MarkSynced();
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
