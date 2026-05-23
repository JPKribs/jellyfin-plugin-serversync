using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.UserSync;

public class UserSyncMergeServiceTests
{
    private static List<LibraryMapping> MakeMappings(params (string src, string loc)[] pairs)
    {
        var list = new List<LibraryMapping>();
        foreach (var (src, loc) in pairs)
        {
            list.Add(new LibraryMapping
            {
                SourceLibraryId = src,
                LocalLibraryId = loc,
                IsEnabled = true
            });
        }

        return list;
    }

    /// <summary>
    /// Known source library IDs translate to their local counterparts.
    /// True: enabled-folders on source map cleanly to local IDs so the policy applies correctly.
    /// False: applied policy would reference source library IDs and lock the user out locally.
    /// </summary>
    [Fact]
    public void TranslateLibraryIds_MapsKnownSourceIdsToLocal()
    {
        var mappings = MakeMappings(("src-a", "loc-a"), ("src-b", "loc-b"));

        var result = UserSyncMergeService.TranslateLibraryIds(new[] { "src-a", "src-b" }, mappings);

        Assert.Equal(new[] { "loc-a", "loc-b" }, result);
    }

    /// <summary>
    /// Unmapped source IDs are silently dropped.
    /// True: the user gets access to only the libraries they're explicitly granted on local.
    /// False: ghost library IDs in policy would either crash apply or grant unintended access.
    /// </summary>
    [Fact]
    public void TranslateLibraryIds_SkipsUnmappedIds()
    {
        var mappings = MakeMappings(("src-a", "loc-a"));

        var result = UserSyncMergeService.TranslateLibraryIds(new[] { "src-a", "src-unknown" }, mappings);

        Assert.Equal(new[] { "loc-a" }, result);
    }

    /// <summary>
    /// Disabled mappings are skipped during translation.
    /// True: operator-disabled mappings produce no library IDs in the merged policy.
    /// False: disabled mappings would silently take effect anyway.
    /// </summary>
    [Fact]
    public void TranslateLibraryIds_SkipsDisabledMappings()
    {
        var mappings = MakeMappings(("src-a", "loc-a"));
        mappings[0].IsEnabled = false;

        var result = UserSyncMergeService.TranslateLibraryIds(new[] { "src-a" }, mappings);

        Assert.Empty(result);
    }

    /// <summary>
    /// Empty input returns empty.
    /// True: callers get back an empty array (not null) and can iterate safely.
    /// False: NullReferenceException at callers iterating the result.
    /// </summary>
    [Fact]
    public void TranslateLibraryIds_EmptyInput_ReturnsEmpty()
    {
        var mappings = MakeMappings(("src-a", "loc-a"));

        var result = UserSyncMergeService.TranslateLibraryIds(System.Array.Empty<string>(), mappings);

        Assert.Empty(result);
    }

    /// <summary>
    /// Null input returns empty without throwing.
    /// True: the helper survives null inputs and returns a safe empty array.
    /// False: a null input from a malformed policy would crash the refresh pass.
    /// </summary>
    [Fact]
    public void TranslateLibraryIds_NullInput_ReturnsEmpty()
    {
        var mappings = MakeMappings(("src-a", "loc-a"));

        var result = UserSyncMergeService.TranslateLibraryIds(null!, mappings);

        Assert.Empty(result);
    }

    /// <summary>
    /// Generic policy fields like IsAdministrator/EnabledFolders are syncable.
    /// True: the most common policy fields flow through unmodified.
    /// False: real policy syncs would be silently no-ops.
    /// </summary>
    [Fact]
    public void ShouldSyncPolicyProperty_IncludesGenericPolicyFields()
    {
        Assert.True(UserSyncMergeService.ShouldSyncPolicyProperty("IsAdministrator"));
        Assert.True(UserSyncMergeService.ShouldSyncPolicyProperty("EnabledFolders"));
    }

    /// <summary>
    /// Server-specific policy fields (channels, devices, login attempts, providers) are blocked.
    /// True: server-specific identifiers can't bleed across servers and break local access.
    /// False: syncing channel/device IDs from source would brick the local user.
    /// </summary>
    [Fact]
    public void ShouldSyncPolicyProperty_ExcludesServerSpecificFields()
    {
        Assert.False(UserSyncMergeService.ShouldSyncPolicyProperty("EnabledChannels"));
        Assert.False(UserSyncMergeService.ShouldSyncPolicyProperty("EnabledDevices"));
        Assert.False(UserSyncMergeService.ShouldSyncPolicyProperty("InvalidLoginAttemptCount"));
        Assert.False(UserSyncMergeService.ShouldSyncPolicyProperty("AuthenticationProviderId"));
        Assert.False(UserSyncMergeService.ShouldSyncPolicyProperty("PasswordResetProviderId"));
    }

    /// <summary>
    /// UI-specific configuration fields are excluded from sync.
    /// True: layout-state IDs that are meaningless across servers don't propagate.
    /// False: syncing UI state would break ordered-views and grouped-folders on the local side.
    /// </summary>
    [Fact]
    public void ShouldSyncConfigurationProperty_ExcludesUiSpecificFields()
    {
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("GroupedFolders"));
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("OrderedViews"));
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("LatestItemsExcludes"));
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("MyMediaExcludes"));
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("EnableLocalPassword"));
        Assert.False(UserSyncMergeService.ShouldSyncConfigurationProperty("CastReceiverId"));
    }

    /// <summary>
    /// Library-ID properties are flagged for translation; others are not.
    /// True: only the two library-ID-bearing properties go through TranslateLibraryIds.
    /// False: incorrect property classification would either skip required translation or run it on the wrong fields.
    /// </summary>
    [Fact]
    public void RequiresLibraryTranslation_KnownProperties()
    {
        Assert.True(UserSyncMergeService.RequiresLibraryTranslation("EnabledFolders"));
        Assert.True(UserSyncMergeService.RequiresLibraryTranslation("EnableContentDeletionFromFolders"));
        Assert.False(UserSyncMergeService.RequiresLibraryTranslation("IsAdministrator"));
    }

    /// <summary>
    /// Null or empty source policy returns null merged policy.
    /// True: a source with no policy returns null so the caller can short-circuit.
    /// False: returning an empty-but-non-null value would queue an empty apply.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_NullOrEmpty_ReturnsNull()
    {
        var mappings = MakeMappings(("src-a", "loc-a"));

        Assert.Null(UserSyncMergeService.ComputeMergedPolicy(null, mappings));
        Assert.Null(UserSyncMergeService.ComputeMergedPolicy(string.Empty, mappings));
    }

    /// <summary>
    /// EnabledFolders in the merged policy contains local IDs, not source IDs.
    /// True: the policy applied locally references libraries that exist locally.
    /// False: applying source IDs locally would either fail or grant unintended access.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_TranslatesEnabledFolders()
    {
        var sourcePolicy = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["IsAdministrator"] = false,
            ["EnabledFolders"] = new[] { "src-a", "src-b" }
        });
        var mappings = MakeMappings(("src-a", "loc-a"), ("src-b", "loc-b"));

        var merged = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, mappings);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(merged!);

        Assert.NotNull(dict);
        var folders = dict["EnabledFolders"].EnumerateArray();
        var ids = new List<string>();
        foreach (var f in folders)
        {
            ids.Add(f.GetString()!);
        }

        Assert.Equal(new[] { "loc-a", "loc-b" }, ids);
    }

    /// <summary>
    /// Non-translated policy properties pass through unchanged.
    /// True: simple scalars (IsAdministrator, MaxParentalRating) survive the merge intact.
    /// False: non-library properties would be mangled or dropped by the merge.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_PassesThroughNonTranslatedProperties()
    {
        var sourcePolicy = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["IsAdministrator"] = true,
            ["MaxParentalRating"] = 18
        });
        var merged = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, MakeMappings());
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(merged!);

        Assert.NotNull(dict);
        Assert.True(dict["IsAdministrator"].GetBoolean());
        Assert.Equal(18, dict["MaxParentalRating"].GetInt32());
    }

    /// <summary>
    /// EnableContentDeletionFromFolders also goes through library-ID translation.
    /// True: the deletion-allowed-from list references local IDs after merge.
    /// False: applying source IDs as deletion targets would silently fail or affect the wrong libraries.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_TranslatesContentDeletionFolders()
    {
        var sourcePolicy = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["EnableContentDeletionFromFolders"] = new[] { "src-a" }
        });
        var mappings = MakeMappings(("src-a", "loc-a"));

        var merged = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, mappings);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(merged!);

        var folders = new List<string>();
        foreach (var f in dict!["EnableContentDeletionFromFolders"].EnumerateArray())
        {
            folders.Add(f.GetString()!);
        }

        Assert.Equal(new[] { "loc-a" }, folders);
    }

    /// <summary>
    /// Malformed source JSON is passed through as-is rather than throwing.
    /// True: defensive — bad inputs surface useful diagnostic info later instead of crashing here.
    /// False: a JsonException would abort the entire refresh pass.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_InvalidJson_ReturnsOriginal()
    {
        var result = UserSyncMergeService.ComputeMergedPolicy("not-json", MakeMappings());

        Assert.Equal("not-json", result);
    }

    /// <summary>
    /// JsonEquals on the service delegates to JsonComparisonUtility.
    /// True: callers can use a single import for both comparator and policy-equality needs.
    /// False: divergent behaviour between the service's JsonEquals and the utility's would surprise callers.
    /// </summary>
    [Fact]
    public void JsonEquals_DelegatesToJsonComparisonUtility()
    {
        Assert.True(UserSyncMergeService.JsonEquals("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}"));
        Assert.False(UserSyncMergeService.JsonEquals("{\"a\":1}", "{\"a\":2}"));
    }
}
