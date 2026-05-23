using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.UserSync;

public class UserSyncItemTests
{
    private static UserSyncItem MakePolicyItem() => new()
    {
        SourceUserId = "src",
        LocalUserId = "loc",
        PropertyCategory = UserPropertyCategory.Policy
    };

    private static UserSyncItem MakeConfigurationItem() => new()
    {
        SourceUserId = "src",
        LocalUserId = "loc",
        PropertyCategory = UserPropertyCategory.Configuration
    };

    private static UserSyncItem MakeProfileImageItem() => new()
    {
        SourceUserId = "src",
        LocalUserId = "loc",
        PropertyCategory = UserPropertyCategory.ProfileImage
    };

    /// <summary>
    /// UpdateMergedValue stores into Value.Source and recomputes the hash.
    /// True: MergedValue, Value.Source, and SourceValueHash are all populated by one call.
    /// False: build-path callers would need to remember three separate assignments and miss one.
    /// </summary>
    [Fact]
    public void UpdateMergedValue_SetsValueSourceAndRecomputesHash()
    {
        var item = MakePolicyItem();

        item.UpdateMergedValue("{\"x\":1}");

        Assert.Equal("{\"x\":1}", item.MergedValue);
        Assert.Equal(item.MergedValue, item.Value.Source);
        Assert.NotNull(item.SourceValueHash);
    }

    /// <summary>
    /// MergedValue setter delegates to Value.Source without touching the hash.
    /// True: table manager can load Source/Hash from DB in any order without overwrite races.
    /// False: the plain setter would silently re-hash and stomp the DB-loaded SourceValueHash.
    /// </summary>
    [Fact]
    public void MergedValue_SetterDelegatesToValueSource_NoHashSideEffect()
    {
        var item = MakePolicyItem();
        item.SourceValueHash = "preset-hash";

        item.MergedValue = "{\"x\":1}";

        Assert.Equal("{\"x\":1}", item.Value.Source);
        Assert.Equal("preset-hash", item.SourceValueHash);
    }

    /// <summary>
    /// LocalValue setter delegates to Value.Local.
    /// True: comparator sees the local-side value via Value.Local during HasChanges.
    /// False: LocalValue would be ignored by the comparator path and rows never queue.
    /// </summary>
    [Fact]
    public void LocalValue_SetterDelegatesToValueLocal()
    {
        var item = MakePolicyItem();

        item.LocalValue = "{\"y\":2}";

        Assert.Equal("{\"y\":2}", item.Value.Local);
    }

    /// <summary>
    /// SourceValueHash setter delegates to Value.SourceHash.
    /// True: table manager populating SourceValueHash on read feeds the fast-path check.
    /// False: hash from DB never reaches the SyncableValue, breaking the short-circuit.
    /// </summary>
    [Fact]
    public void SourceValueHash_SetterDelegatesToValueSourceHash()
    {
        var item = MakePolicyItem();

        item.SourceValueHash = "abc";

        Assert.Equal("abc", item.Value.SourceHash);
    }

    /// <summary>
    /// SyncedValueHash setter delegates to Value.SyncedHash.
    /// True: post-apply SyncedHash flows from DB into the SyncableValue for the fast path.
    /// False: short-circuit can't fire because SyncedHash is null on the SyncableValue.
    /// </summary>
    [Fact]
    public void SyncedValueHash_SetterDelegatesToValueSyncedHash()
    {
        var item = MakePolicyItem();

        item.SyncedValueHash = "def";

        Assert.Equal("def", item.Value.SyncedHash);
    }

    /// <summary>
    /// Policy HasChanges short-circuits on hash match, even with a divergent LocalValue.
    /// True: a stable source policy doesn't re-queue rows whose local just happens to differ.
    /// False: every refresh would re-queue Policy rows for no reason.
    /// </summary>
    [Fact]
    public void HasChanges_Policy_ShortCircuitsOnHashMatch()
    {
        var item = MakePolicyItem();
        item.UpdateMergedValue("{\"IsAdministrator\":true}");
        item.MarkSynced();
        item.LocalValue = "{\"IsAdministrator\":false}";

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Source moves + LocalValue diverges → HasChanges fires.
    /// True: a real policy change is queued for sync.
    /// False: policy changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasChanges_Policy_DetectsRealDiff_AfterSourceMoves()
    {
        var item = MakePolicyItem();
        item.UpdateMergedValue("{\"IsAdministrator\":true}");
        item.MarkSynced();

        item.UpdateMergedValue("{\"IsAdministrator\":false}");
        item.LocalValue = "{\"IsAdministrator\":true}";

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Fresh Policy row where source matches local returns no changes.
    /// True: idempotent first refresh doesn't pointlessly queue every row.
    /// False: every first-time refresh would queue every Policy row.
    /// </summary>
    [Fact]
    public void HasChanges_Policy_FreshRow_NoChanges_IsFalse()
    {
        var item = MakePolicyItem();
        item.UpdateMergedValue("{\"a\":1}");
        item.LocalValue = "{\"a\":1}";

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Configuration HasChanges detects a real merged-vs-local diff.
    /// True: configuration changes (e.g. PlayDefaultAudioTrack) queue for sync.
    /// False: configuration changes would silently never propagate.
    /// </summary>
    [Fact]
    public void HasChanges_Configuration_DetectsDiff()
    {
        var item = MakeConfigurationItem();
        item.UpdateMergedValue("{\"PlayDefaultAudioTrack\":true}");
        item.LocalValue = "{\"PlayDefaultAudioTrack\":false}";

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// ProfileImage HasChanges is false when hashes match.
    /// True: synced images stay Synced and don't re-queue.
    /// False: every refresh would redownload the same image.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_HashMatch_IsFalse()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = "abc123";
        item.LocalImageHash = "abc123";
        item.SourceImageSize = 5000;
        item.LocalImageSize = 5000;

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// ProfileImage HasChanges is true when hashes differ.
    /// True: a new profile image on source queues for sync.
    /// False: changed profile images would never propagate.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_HashDiffers_IsTrue()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = "abc123";
        item.LocalImageHash = "def456";

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Source cleared the image but local still has one → HasChanges fires.
    /// True: a profile-image deletion on source propagates as a local deletion.
    /// False: cleared source images would stay forever on local.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_SourceRemovedImage_LocalStillHas_IsTrue()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = null;
        item.SourceImageSize = 0;
        item.LocalImageHash = "local-hash";
        item.LocalImageSize = 5000;

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Neither side has a profile image → no changes.
    /// True: an empty/empty profile-image row is correctly idempotent.
    /// False: empty rows would queue an apply that downloads nothing and then fails verify.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_NeitherHasImage_IsFalse()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = null;
        item.SourceImageSize = 0;
        item.LocalImageHash = null;
        item.LocalImageSize = 0;

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// No SourceImageHash but different sizes still trigger a sync.
    /// True: size fallback detects divergence when hashes aren't available.
    /// False: image diffs without computed hashes would go undetected.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_NoSourceHash_SizeFallback_Differs()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = null;
        item.SourceImageSize = 5000;
        item.LocalImageSize = 3000;

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// No SourceImageHash but matching sizes mark the row as Synced.
    /// True: size-fallback equality doesn't pointlessly re-queue rows where the bytes likely match.
    /// False: rows would always queue when hash is unavailable, redownloading on every refresh.
    /// </summary>
    [Fact]
    public void HasChanges_ProfileImage_NoSourceHash_SizeFallback_Matches()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = null;
        item.SourceImageSize = 5000;
        item.LocalImageSize = 5000;

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Policy MarkSynced delegates to Value.MarkSynced.
    /// True: next refresh sees SourceHash == SyncedHash and short-circuits.
    /// False: SyncedHash stays null, the fast path never fires for Policy.
    /// </summary>
    [Fact]
    public void MarkSynced_Policy_DelegatesToValueMarkSynced()
    {
        var item = MakePolicyItem();
        item.UpdateMergedValue("{\"a\":1}");

        item.MarkSynced();

        Assert.Equal(item.Value.SourceHash, item.Value.SyncedHash);
    }

    /// <summary>
    /// Configuration MarkSynced delegates to Value.MarkSynced.
    /// True: next refresh sees SourceHash == SyncedHash and short-circuits.
    /// False: SyncedHash stays null, the fast path never fires for Configuration.
    /// </summary>
    [Fact]
    public void MarkSynced_Configuration_DelegatesToValueMarkSynced()
    {
        var item = MakeConfigurationItem();
        item.UpdateMergedValue("{\"a\":1}");

        item.MarkSynced();

        Assert.Equal(item.Value.SourceHash, item.Value.SyncedHash);
    }

    /// <summary>
    /// ProfileImage MarkSynced copies SourceImage* into SyncedImage*.
    /// True: post-apply hash and size are captured so future refreshes can short-circuit.
    /// False: SyncedImage* stays null and the row would re-queue on every refresh.
    /// </summary>
    [Fact]
    public void MarkSynced_ProfileImage_CopiesImageHashAndSize()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = "abc123";
        item.SourceImageSize = 5000;

        Assert.Null(item.SyncedImageHash);

        item.MarkSynced();

        Assert.Equal("abc123", item.SyncedImageHash);
        Assert.Equal(5000, item.SyncedImageSize);
    }

    /// <summary>
    /// ProfileImage MarkSynced does not touch the SyncableValue's SyncedHash.
    /// True: stale Value state from a prior Policy/Config refresh on the same row never leaks here.
    /// False: cross-category contamination via Value.SyncedHash could falsely short-circuit Policy/Config.
    /// </summary>
    [Fact]
    public void MarkSynced_ProfileImage_DoesNotTouchValueHash()
    {
        var item = MakeProfileImageItem();
        item.SourceValueHash = "value-src";
        item.SourceImageHash = "img-src";

        item.MarkSynced();

        Assert.Equal("img-src", item.SyncedImageHash);
        Assert.Null(item.Value.SyncedHash);
    }

    /// <summary>
    /// ChangesSummary returns "No changes" for a no-diff row.
    /// True: synced rows show the expected sentinel in the UI.
    /// False: noisy summaries on idempotent rows mislead the operator.
    /// </summary>
    [Fact]
    public void ChangesSummary_NoChanges_ReturnsNoChanges()
    {
        var item = MakePolicyItem();
        item.UpdateMergedValue("{\"a\":1}");
        item.LocalValue = "{\"a\":1}";

        Assert.Equal("No changes", item.ChangesSummary);
    }

    /// <summary>
    /// ProfileImage ChangesSummary formats the source image size.
    /// True: operator sees the size of the incoming image in the summary cell.
    /// False: cell would say "No changes" or be empty even when an apply is queued.
    /// </summary>
    [Fact]
    public void ChangesSummary_ProfileImage_FormatsSize()
    {
        var item = MakeProfileImageItem();
        item.SourceImageHash = "src";
        item.LocalImageHash = null;
        item.SourceImageSize = 1024;

        var summary = item.ChangesSummary;

        Assert.NotEqual("No changes", summary);
        Assert.NotEmpty(summary);
    }
}
