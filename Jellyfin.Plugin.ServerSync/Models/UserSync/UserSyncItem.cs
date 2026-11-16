using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// One row per user mapping and property category.
/// Policy and Configuration detect changes through a <see cref="SyncableValue{T}"/>, and ProfileImage through its own pair of image hashes.
/// </summary>
public class UserSyncItem : SyncRecord
{
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

    /// <summary>
    /// Gets or sets the property category (Policy, Configuration, ProfileImage).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source's raw value, for display only.
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Gets or sets the value the scan read from this server, held as the local side of <see cref="Value"/>.
    /// </summary>
    public string? LocalValue
    {
        get => Value.Local;
        set => Value.Local = value;
    }

    /// <summary>
    /// Gets or sets the value to apply, held as the source side of <see cref="Value"/>.
    /// A scan sets it through <see cref="UpdateMergedValue"/> so the source hash is computed too.
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
    /// Sets <see cref="MergedValue"/> and computes its hash through the comparator.
    /// </summary>
    /// <param name="mergedValue">The value after merging both sides.</param>
    public void UpdateMergedValue(string? mergedValue) => Value.UpdateSource(mergedValue);

    /// <summary>
    /// Gets or sets the source profile image hash (SHA256, truncated).
    /// </summary>
    public string? SourceImageHash { get; set; }

    /// <summary>
    /// Gets or sets the local profile image hash (SHA256, truncated).
    /// </summary>
    public string? LocalImageHash { get; set; }

    /// <summary>
    /// Gets or sets the image hash last applied.
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

    /// <inheritdoc />
    public override bool HasChanges
    {
        get
        {
            if (PropertyCategory == UserPropertyCategory.ProfileImage)
            {
                // An image the source removed but this server still holds is queued for removal.
                return ImageUtilities.PictureDiffers(SourceImageHash, SourceImageSize, LocalImageHash, LocalImageSize);
            }

            return Value.HasChanges;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether this run only recorded what the row's server holds, because another server speaks for the user.
    /// It is not stored.
    /// </summary>
    public bool Observed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the last agreed value is kept because this server's own change has not reached the other server yet.
    /// It is not stored.
    /// </summary>
    public bool KeepAgreedValue { get; set; }

    /// <inheritdoc />
    public override void MarkSynced()
    {
        if (Observed || KeepAgreedValue)
        {
            return;
        }

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
