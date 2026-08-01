using System.Collections.Generic;
using System.Linq;
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

    // ===================================================================
    // GUID exclusion. Identifiers are per-install, so a source library /
    // channel / user GUID written into a local user's policy either grants
    // nothing or leaves a dangling reference. These run against the REAL
    // Jellyfin and SDK types so a model change upstream fails the build
    // rather than silently resuming the leak.
    // ===================================================================

    /// <summary>
    /// The type walker recognises every shape a Guid reaches us in.
    /// True: Guid[], List&lt;Guid?&gt;, and complex types embedding a Guid are all caught.
    /// False: the shapes it misses get copied across servers verbatim.
    /// </summary>
    [Fact]
    public void IsGuidBearingType_RecognisesEveryGuidShape()
    {
        Assert.True(UserSyncMergeService.IsGuidBearingType(typeof(System.Guid)));
        Assert.True(UserSyncMergeService.IsGuidBearingType(typeof(System.Guid?)));
        Assert.True(UserSyncMergeService.IsGuidBearingType(typeof(System.Guid[])));
        Assert.True(UserSyncMergeService.IsGuidBearingType(typeof(List<System.Guid?>)));

        // AccessSchedule is not a Guid, but it carries the source user's UserId.
        Assert.True(UserSyncMergeService.IsGuidBearingType(
            typeof(Jellyfin.Database.Implementations.Entities.AccessSchedule)));
        Assert.True(UserSyncMergeService.IsGuidBearingType(
            typeof(Jellyfin.Database.Implementations.Entities.AccessSchedule[])));
    }

    /// <summary>
    /// Guid-free types must still sync — the exclusion has to be surgical.
    /// True: booleans, strings, enums, and string collections pass through.
    /// False: over-broad exclusion silently stops syncing ordinary settings.
    /// </summary>
    [Fact]
    public void IsGuidBearingType_LeavesOrdinaryTypesAlone()
    {
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(bool)));
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(bool?)));
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(string)));
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(string[])));
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(List<string>)));
        Assert.False(UserSyncMergeService.IsGuidBearingType(typeof(int?)));
        Assert.False(UserSyncMergeService.IsGuidBearingType(
            typeof(Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode)));
    }

    /// <summary>
    /// Exhaustive contract over the live models: nothing Guid-bearing may be
    /// declared syncable, on either the local entity type or the SDK type.
    /// True: no source identifier can reach a local user's policy or configuration.
    /// False: fields like BlockedMediaFolders / BlockedChannels / AccessSchedules
    /// copy source GUIDs into local rows, which is exactly the leak this guards.
    /// </summary>
    [Theory]
    [InlineData(typeof(MediaBrowser.Model.Users.UserPolicy), true)]
    [InlineData(typeof(Jellyfin.Sdk.Generated.Models.UserPolicy), true)]
    [InlineData(typeof(MediaBrowser.Model.Configuration.UserConfiguration), false)]
    [InlineData(typeof(Jellyfin.Sdk.Generated.Models.UserConfiguration), false)]
    public void NoGuidBearingPropertyIsSyncable(System.Type modelType, bool isPolicy)
    {
        var leaked = new List<string>();

        foreach (var property in modelType.GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!UserSyncMergeService.IsGuidBearingType(property.PropertyType))
            {
                continue;
            }

            var syncable = isPolicy
                ? UserSyncMergeService.ShouldSyncPolicyProperty(property)
                : UserSyncMergeService.ShouldSyncConfigurationProperty(property);

            if (syncable)
            {
                leaked.Add(property.Name);
            }
        }

        Assert.True(
            leaked.Count == 0,
            $"{modelType.Name} would sync Guid-bearing propert(ies): {string.Join(", ", leaked)}");
    }

    /// <summary>
    /// End-to-end on the extracted blob: the serialized policy must not contain
    /// any of the known identifier fields.
    /// True: the JSON actually written to the sync table is identifier-free.
    /// False: the filter is right but the extraction path bypasses it.
    /// </summary>
    [Fact]
    public void ExtractPolicyJson_OmitsIdentifierFields()
    {
        var policy = new MediaBrowser.Model.Users.UserPolicy
        {
            IsAdministrator = true,
            EnabledFolders = new[] { System.Guid.NewGuid() },
            BlockedMediaFolders = new[] { System.Guid.NewGuid() },
            EnabledChannels = new[] { System.Guid.NewGuid() },
            BlockedChannels = new[] { System.Guid.NewGuid() },
            EnabledDevices = new[] { "device-a" },
            EnableContentDeletionFromFolders = new[] { System.Guid.NewGuid().ToString() }
        };

        var json = UserSyncMergeService.ExtractPolicyJson(policy);
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

        foreach (var excluded in new[]
        {
            "EnabledFolders", "BlockedMediaFolders", "EnabledChannels", "BlockedChannels",
            "EnabledDevices", "EnableContentDeletionFromFolders", "AccessSchedules"
        })
        {
            Assert.False(props.ContainsKey(excluded), $"{excluded} must not be synced");
        }

        // Ordinary settings still come across.
        Assert.True(props.ContainsKey("IsAdministrator"));
        Assert.True(props.ContainsKey("EnableAllFolders"));
    }

    /// <summary>
    /// Same contract for the configuration blob.
    /// </summary>
    [Fact]
    public void ExtractConfigurationJson_OmitsIdentifierFields()
    {
        var config = new MediaBrowser.Model.Configuration.UserConfiguration
        {
            PlayDefaultAudioTrack = true,
            GroupedFolders = new[] { System.Guid.NewGuid() },
            OrderedViews = new[] { System.Guid.NewGuid() },
            LatestItemsExcludes = new[] { System.Guid.NewGuid() },
            MyMediaExcludes = new[] { System.Guid.NewGuid() }
        };

        var json = UserSyncMergeService.ExtractConfigurationJson(config);
        var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

        foreach (var excluded in new[]
        {
            "GroupedFolders", "OrderedViews", "LatestItemsExcludes", "MyMediaExcludes", "CastReceiverId"
        })
        {
            Assert.False(props.ContainsKey(excluded), $"{excluded} must not be synced");
        }

        Assert.True(props.ContainsKey("PlayDefaultAudioTrack"));
        Assert.True(props.ContainsKey("SubtitleMode"));
    }

    // ===================================================================
    // Settling. With the SourceHash short-circuit removed, HasChanges runs
    // the comparator on every refresh. If the source-side and local-side
    // blobs can never be made equal for an unchanged user — mismatched enum
    // numbering, a differing key set between the SDK type and the local
    // entity type — the row requeues forever instead of going quiet after
    // one sync. These round-trip the REAL models to prove they converge.
    // ===================================================================

    /// <summary>
    /// Enum members shared by the local and SDK models must carry identical
    /// numeric values. Both blobs serialize enums as numbers, so a numbering
    /// mismatch is a diff no apply can ever close.
    /// True: enum-valued settings converge after a sync.
    /// False: the row requeues on every run forever, rewriting the local user
    /// each time — the failure mode the removed hash short-circuit used to hide.
    /// </summary>
    [Fact]
    public void EnumsSerializedIntoBlobs_HaveMatchingNumericValues()
    {
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.CreateAndJoinGroups,
            (int)Jellyfin.Sdk.Generated.Models.UserPolicy_SyncPlayAccess.CreateAndJoinGroups);
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.JoinGroups,
            (int)Jellyfin.Sdk.Generated.Models.UserPolicy_SyncPlayAccess.JoinGroups);
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.None,
            (int)Jellyfin.Sdk.Generated.Models.UserPolicy_SyncPlayAccess.None);

        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.Default,
            (int)Jellyfin.Sdk.Generated.Models.UserConfiguration_SubtitleMode.Default);
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.Smart,
            (int)Jellyfin.Sdk.Generated.Models.UserConfiguration_SubtitleMode.Smart);
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.OnlyForced,
            (int)Jellyfin.Sdk.Generated.Models.UserConfiguration_SubtitleMode.OnlyForced);
        Assert.Equal(
            (int)Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.None,
            (int)Jellyfin.Sdk.Generated.Models.UserConfiguration_SubtitleMode.None);
    }

    /// <summary>
    /// Extraction is deterministic: the same policy yields byte-identical JSON.
    /// True: an unchanged user compares equal on the cheap ordinal fast path.
    /// False: identical state serializes differently run to run and every row
    /// requeues forever.
    /// </summary>
    [Fact]
    public void ExtractPolicyJson_IsDeterministic()
    {
        var policy = new MediaBrowser.Model.Users.UserPolicy
        {
            IsAdministrator = true,
            MaxActiveSessions = 3,
            AllowedTags = new[] { "tag-a", "tag-b" },
            SyncPlayAccess = Jellyfin.Database.Implementations.Enums.SyncPlayUserAccessType.CreateAndJoinGroups
        };

        var first = UserSyncMergeService.ExtractPolicyJson(policy);
        var second = UserSyncMergeService.ExtractPolicyJson(policy);

        Assert.Equal(first, second);
        Assert.True(UserSyncMergeService.JsonEquals(first, second));
    }

    /// <summary>
    /// A merged blob round-tripped through ComputeMergedPolicy still compares
    /// equal to its input, so a settled row stays settled.
    /// True: the merge step is idempotent and does not itself create a diff.
    /// False: merging perturbs the blob and the row never converges.
    /// </summary>
    [Fact]
    public void ComputeMergedPolicy_IsIdempotent()
    {
        var policy = new MediaBrowser.Model.Users.UserPolicy
        {
            IsAdministrator = true,
            MaxActiveSessions = 3,
            AllowedTags = new[] { "tag-a" }
        };

        var extracted = UserSyncMergeService.ExtractPolicyJson(policy);
        var once = UserSyncMergeService.ComputeMergedPolicy(extracted, MakeMappings());
        var twice = UserSyncMergeService.ComputeMergedPolicy(once, MakeMappings());

        Assert.True(UserSyncMergeService.JsonEquals(once, twice));
        Assert.True(UserSyncMergeService.JsonEquals(extracted, once));
    }

    /// <summary>
    /// The two model types must expose the same syncable key set. A key present
    /// on one side only is a permanent diff no apply can close.
    /// True: source and local blobs are comparable field for field.
    /// False: rows never settle regardless of the values in them.
    /// </summary>
    [Theory]
    [InlineData(typeof(MediaBrowser.Model.Users.UserPolicy), typeof(Jellyfin.Sdk.Generated.Models.UserPolicy), true)]
    [InlineData(typeof(MediaBrowser.Model.Configuration.UserConfiguration), typeof(Jellyfin.Sdk.Generated.Models.UserConfiguration), false)]
    public void SyncableKeySets_MatchAcrossLocalAndSdkModels(System.Type localType, System.Type sdkType, bool isPolicy)
    {
        static System.Collections.Generic.HashSet<string> Keys(System.Type t, bool policy)
        {
            var keys = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var p in t.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var ok = policy
                    ? UserSyncMergeService.ShouldSyncPolicyProperty(p)
                    : UserSyncMergeService.ShouldSyncConfigurationProperty(p);
                if (ok)
                {
                    keys.Add(p.Name);
                }
            }

            return keys;
        }

        var localKeys = Keys(localType, isPolicy);
        var sdkKeys = Keys(sdkType, isPolicy);

        var localOnly = localKeys.Except(sdkKeys).ToList();
        var sdkOnly = sdkKeys.Except(localKeys).ToList();

        Assert.True(
            localOnly.Count == 0 && sdkOnly.Count == 0,
            $"Syncable key sets diverge. local-only: [{string.Join(", ", localOnly)}] sdk-only: [{string.Join(", ", sdkOnly)}]");
    }
}
