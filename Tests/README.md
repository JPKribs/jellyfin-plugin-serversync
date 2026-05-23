# Tests

## How to run

```sh
# From the repo root:
dotnet test
```

## Tests

### Common

| Test Name | Summary |
| --- | --- |
| `HasChanges_IsFalse_WhenSourceHashEqualsSyncedHash` | The SourceHash == SyncedHash fast path suppresses the deep compare. |
| `HasChanges_IsTrue_WhenSourceHashDiffersFromSyncedHash_AndSourceDiffersFromLocal` | Source moved and Source still differs from Local, so HasChanges fires. |
| `HasChanges_IsFalse_WhenSourceMatchesLocal_AndHashesUnseeded` | Fresh row where Source equals Local returns no changes. |
| `HasChanges_IsTrue_WhenSourceDiffersFromLocal_AndHashesUnseeded` | Fresh row with Source != Local fires changes via comparator. |
| `UpdateSource_AssignsSourceAndRecomputesHash` | UpdateSource assigns Source and recomputes the source hash in one step. |
| `UpdateSource_WithNull_ResultsInNullHash` | UpdateSource(null) clears Source and produces a null hash. |
| `RecomputeSourceHash_StableForSameInput` | Hashing the same JSON twice produces the same fingerprint. |
| `RecomputeSourceHash_DiffersForDifferentInputs` | Different JSON content produces different hashes. |
| `MarkSynced_CopiesSourceToSyncedAndSourceHashToSyncedHash` | MarkSynced copies Source/SourceHash into Synced/SyncedHash. |
| `MarkSynced_AfterSourceMoves_SyncedHashCatchesUp` | A second MarkSynced after source moves updates SyncedHash to the new source. |
| `ComputeHash_ReturnsNullForNullOrEmpty` | JsonBlobComparator returns null hash for null/empty input. |
| `ComputeHash_StableForSameInput` | JsonBlobComparator produces the same hash across calls for identical input. |
| `ComputeHash_DiffersForDifferentJson` | JsonBlobComparator yields different hashes for different content. |
| `ComputeHash_ProducesLowercaseHexFullSha256` | JsonBlobComparator hash is full 64-character lowercase SHA256 hex. |
| `Equals_TreatsKeyOrderingAsSemanticallyEqual` | Same keys in different order compare equal. |
| `Equals_DistinguishesDifferentValues` | Different values on the same key compare not-equal. |
| `Equals_HandlesBothNull` | Both null compare equal. |
| `Equals_HandlesOneNull` | One side null and other side populated compare not-equal without throwing. |
| `Equals_EmptyOrNullSource_IsTreatedAsMatch` | ImageManifestComparator treats null/empty source as "nothing to sync". |
| `Equals_EmptyLocal_NonEmptySource_IsDifferent` | Non-empty source with empty local compares not-equal. |
| `Equals_MatchingSizes_AreEqual` | Identical type/index/size manifests compare equal. |
| `Equals_DifferentSizes_AreDifferent` | Same image at different sizes compares not-equal. |
| `Equals_TagOnlySource_SizedLocal_IsDifferent` | Source with Size=0 (tag-only) and a sized local compare not-equal. |
| `Equals_SizedSource_MissingLocalFile_IsDifferent` | Sized source and Size=0 local (missing file) compare not-equal. |
| `Equals_BothZeroSize_TreatedAsMatch` | Both sides Size=0 (indeterminate) compare equal. |
| `Equals_MissingTypeOnLocal_IsDifferent` | Source has an image type that local doesn't, compare not-equal. |
| `Equals_DifferentCount_IsDifferent` | Different image count for the same type compares not-equal. |
| `Equals_ExtraLocalTypes_TolerantOfLocalSuperset` | Local has extra types beyond source — comparator tolerates this. |
| `ComputeHash_StableForSameInput` | ImageManifest hash is stable for same manifest. |
| `ComputeHash_TagChange_ChangesHash` | Different image Tag with same size produces different hash. |
| `ComputeHash_NullOrEmpty_ReturnsNull` | ImageManifest null/empty input produces a null hash. |
| `ComputeHash_EmptyMap_ReturnsNull` | Serialised empty map (`{}`) hashes to null. |
| `DescribeDifference_NamesTheDivergingType` | DescribeDifference points at the specific type and sizes that diverged. |
| `JsonEquals_BothNullOrEmpty_IsTrue` | Both inputs null or empty compare equal. |
| `JsonEquals_KeyOrderInsensitive` | Object equality is independent of key ordering. |
| `JsonEquals_NestedObjects_KeyOrderInsensitive` | Nested object equality is also order-insensitive. |
| `JsonEquals_DistinguishesValues` | Different values on the same key compare not-equal. |
| `JsonEquals_ArrayOrderMatters` | Arrays compare positionally, not as sets. |
| `JsonEquals_InvalidJson_FallsBackToStringCompare` | Non-JSON inputs fall back to string comparison. |
| `CountDifferences_ZeroForIdenticalObjects` | Identical objects produce zero differences. |
| `CountDifferences_OneForSingleFieldChange` | One field change produces a count of 1. |
| `CountDifferences_HandlesMissingField` | Missing fields on one side count as a difference. |
| `GetDifferingFields_ReturnsFieldNames` | GetDifferingFields names the specific fields that diverge. |
| `GetDifferingFields_EmptyForIdenticalObjects` | Identical objects return an empty diff list. |
| `DateOnlyEquals_SameDate_DifferentTimeOfDay_IsTrue` | Same date with different time-of-day compares equal date-only. |
| `DateOnlyEquals_DifferentDays_IsFalse` | Different calendar dates compare not-equal. |
| `DateOnlyEquals_BothNull_IsTrue` | Both null compare equal. |
| `DateOnlyEquals_OneNull_IsFalse` | Set vs unset date compares not-equal in either direction. |

### ContentSync

| Test Name | Summary |
| --- | --- |
| `HasChanges_Queued` | Queued rows report HasChanges. |
| `HasChanges_Deleting` | Deleting rows report HasChanges. |
| `HasChanges_PendingWithPendingType` | Pending rows with a PendingType report HasChanges. |
| `HasChanges_PendingWithoutPendingType` | Pending rows without a PendingType report no changes. |
| `HasChanges_Synced` | Synced rows report no changes. |
| `HasChanges_Errored` | Errored rows report no changes (retry path is separate). |
| `HasChanges_Ignored` | Ignored rows report no changes. |
| `MarkSynced_IsNoOp` | MarkSynced is a no-op for ContentSync (no SyncableValue fields). |

### HistorySync

| Test Name | Summary |
| --- | --- |
| `MergeHistoryData_IsFavorite_AlwaysFromSource` | IsFavorite always takes the source value. |
| `MergeHistoryData_IsFavorite_FalseWhenSourceNull` | Null SourceIsFavorite defaults the merged value to false. |
| `MergeHistoryData_PlayCount_IsMaxOfSourceAndLocal` | PlayCount takes MAX(source, local). |
| `MergeHistoryData_PlayCount_HandlesNullsAsZero` | Null PlayCount is treated as zero for MAX comparison. |
| `MergeHistoryData_NegotiatedFields_SourceWinsWhenMoreRecent` | Source wins Played/Position/LastPlayed when source has the more-recent date. |
| `MergeHistoryData_NegotiatedFields_LocalWinsWhenMoreRecent` | Local wins Played/Position/LastPlayed when local has the more-recent date. |
| `MergeHistoryData_OnlySourceHasDate_TakesSourceValues` | Only source has a LastPlayed → its values are used. |
| `MergeHistoryData_OnlyLocalHasDate_TakesLocalValues` | Only local has a LastPlayed → its values are used. |
| `MergeHistoryData_NeitherDate_FallsBackToSourceValues` | Neither side has a date → fall back to source values. |
| `MergeHistoryData_EqualDates_FallsToSource` | Equal LastPlayedDate ties favour source. |
| `HasChangesToSync_PlayedDifference_IsTrue` | A merged-vs-local IsPlayed mismatch is detected as a change. |
| `HasChangesToSync_PlayCountDifference_IsTrue` | A merged-vs-local PlayCount mismatch is detected as a change. |
| `HasChangesToSync_FavoriteDifference_IsTrue` | A merged-vs-local Favorite mismatch is detected as a change. |
| `HasChangesToSync_PositionDifference_IsTrue` | A merged-vs-local PlaybackPosition mismatch is detected as a change. |
| `HasChangesToSync_LastPlayedDateDifference_IsTrue` | A merged-vs-local LastPlayedDate mismatch is detected as a change. |
| `HasChangesToSync_AllMatch_IsFalse` | All merged fields equal their local counterparts — no change to sync. |
| `HasChangesToSync_NoLocalItemId_WithMergeDiff_StillReturnsTrue_FlaggedDeadCode` | Pins the surprising current behaviour: LocalItemId guard is unreachable. |
| `HasChangesToSync_NoLocalItemId_NoMergeDiff_ReturnsFalse` | No LocalItemId with no merge diff returns false (the intended guard path). |
| `GetChangeSummary_NoChanges_ReturnsNoChanges` | Default item with no changes returns the "No changes" sentinel. |
| `GetChangeSummary_PlayedChange_IsListed` | A Played change is listed by name with the True/False transition shown. |
| `GetChangeSummary_MultipleChanges_AllListed` | Multiple field changes are all listed in the summary. |
| `UpdateSourceStateBundle_ProducesSameHashForSameSourceFields` | Identical source fields produce identical bundle hashes. |
| `UpdateSourceStateBundle_DifferentFields_ProducesDifferentHash` | Changing any source field changes the bundle hash. |
| `HasChanges_ShortCircuits_WhenSourceHashEqualsSyncedHash` | Hash short-circuits HasChanges even with a merge diff. |
| `HasChanges_FallsThroughToMerge_WhenSourceHashDiffers` | Source moved → fall through to the merge service. |
| `HasChanges_FreshRow_NoSyncedHash_UsesMergeFallback` | Fresh row (never synced) uses the merge fallback. |
| `HasChanges_FreshRow_NoChanges_IsFalse` | Fresh row with no diff returns no changes. |
| `MarkSynced_CopiesSourceStateHashes` | MarkSynced copies the SourceState hash to the synced hash. |
| `MarkSynced_AfterSourceMoves_SyncedHashCatchesUp` | MarkSynced after source moves updates SyncedHash to the latest source value. |

### MetadataSync

| Test Name | Summary |
| --- | --- |
| `MergeMetadataFields_PopulatesSourceBlob_FromBaseItemDto` | Source-side metadata blob is populated from BaseItemDto. |
| `MergeMetadataFields_SyncGenresFalse_OmitsGenresField` | Genres omitted from blob when syncGenres is false. |
| `MergeMetadataFields_SyncGenresTrue_IncludesNormalizedGenres` | Genres included in blob when syncGenres is true. |
| `MergeMetadataFields_SyncTagsToggleControlsTagsField` | syncTags toggles whether Tags are included in the source blob. |
| `MergeMetadataFields_RecomputesSourceHash` | MergeMetadataFields populates Metadata.SourceHash. |
| `MergeMetadataFields_ProducesStableHash_ForIdenticalInputs` | Two identical DTO inputs produce identical SourceHash values. |
| `MergeMetadataFields_ProducesDifferentHash_ForDifferentGenreFlags` | Toggling syncGenres changes the SourceHash for the same DTO. |
| `MergeStudios_NullSourceStudios_SetsEmptyArray` | No source studios yields an empty-array Studios.Source. |
| `MergeStudios_FiltersWhitespaceNames` | Studio names with only whitespace are filtered from the source blob. |
| `MergeStudios_SortsAlphabetically` | Studios are sorted alphabetically before serialization. |
| `MergeStudios_NullLocalItem_LeavesLocalStudiosNull` | Null local item leaves Studios.Local untouched. |
| `MergePeople_NoSourcePeople_SetsEmptyArray` | No source people yields an empty-array People.Source. |
| `MergePeople_NullLocalItem_LeavesLocalPeopleNull` | Null local item leaves People.Local untouched. |
| `HasChangesToSync_PassesThroughToItemHasChanges` | HasChangesToSync delegates to the record's HasChanges. |
| `GetChangeSummary_NoChanges_ReturnsNoChanges` | Idempotent row returns the "No changes" sentinel. |
| `GetChangeSummary_ListsChangedCategories` | Summary lists each changed category by name. |
| `HasMetadataChanges_NoLocalItemId_IsFalse` | HasMetadataChanges is false when LocalItemId is missing. |
| `HasMetadataChanges_NoSource_IsFalse` | HasMetadataChanges is false when Metadata.Source is empty. |
| `HasMetadataChanges_Diff_IsTrue` | HasMetadataChanges is true when source and local are present and differ. |
| `HasImagesChanges_NoLocalItemId_IsFalse` | HasImagesChanges is false when LocalItemId is missing. |
| `HasImagesChanges_Diff_IsTrue` | HasImagesChanges is true when source and local manifests differ. |
| `HasPeopleChanges_NoLocalItemId_IsFalse` | HasPeopleChanges is false when LocalItemId is missing. |
| `HasPeopleChanges_Diff_IsTrue` | HasPeopleChanges is true when source and local people differ. |
| `HasStudiosChanges_EmptySource_IsFalse` | HasStudiosChanges is false when Studios.Source is empty array. |
| `HasStudiosChanges_NoLocalItemId_IsFalse` | HasStudiosChanges is false when LocalItemId is missing. |
| `HasStudiosChanges_Diff_IsTrue` | HasStudiosChanges is true when source has studios that differ from local. |
| `HasChanges_AggregatesAcrossCategories` | HasChanges aggregates over all four categories. |
| `MarkSynced_AdvancesAllCategoryHashes` | MarkSynced calls MarkSynced on all four categories. |

### PeopleSync

| Test Name | Summary |
| --- | --- |
| `BuildSourceMetadata_ReturnsValidJsonBlob` | BuildSourceMetadata returns valid JSON that deserialises to an object. |
| `BuildSourceMetadata_StableForSameInputs` | Two identical inputs produce identical blob strings. |
| `BuildSourceMetadata_SortsTagsAndProductionLocations` | Tags and ProductionLocations are sorted alphabetically. |
| `BuildSourceMetadata_LockData_DefaultsToFalseWhenNull` | Null LockData on the source defaults to false in the blob. |
| `PopulateImageData_NullInputs_ReturnsNulls` | Null source and null local return null source/local values. |
| `PopulateImageData_SourceWithImageTag_LocalNull_ReturnsSourceOnly` | Source-with-image and null local returns just the source-side manifest. |
| `PopulateImageData_SourceWithNoImageTags_ReturnsNullSource` | DTO without ImageTags returns null source-image manifest. |
| `HasChangesToSync_PassesThroughToItemHasChanges` | HasChangesToSync delegates to the record's HasChanges. |
| `GetChangeSummary_NoChanges_ReturnsNoChanges` | Idempotent row returns "No changes". |
| `GetChangeSummary_ListsChangedCategories` | Summary lists each changed category by name. |
| `HasMetadataChanges_NoLocalPersonId_IsFalse` | HasMetadataChanges is false when LocalPersonId is missing. |
| `HasMetadataChanges_Diff_IsTrue` | HasMetadataChanges is true when source and local differ. |
| `HasImagesChanges_NoLocalPersonId_IsFalse` | HasImagesChanges is false when LocalPersonId is missing. |
| `HasImagesChanges_Diff_IsTrue` | HasImagesChanges is true when source and local manifests differ. |
| `HasChanges_AggregatesAcrossCategories` | HasChanges aggregates over both categories. |
| `MarkSynced_AdvancesBothCategoryHashes` | MarkSynced advances hashes on both categories. |

### UserSync

| Test Name | Summary |
| --- | --- |
| `TranslateLibraryIds_MapsKnownSourceIdsToLocal` | Known source library IDs translate to their local counterparts. |
| `TranslateLibraryIds_SkipsUnmappedIds` | Unmapped source IDs are silently dropped. |
| `TranslateLibraryIds_SkipsDisabledMappings` | Disabled mappings are skipped during translation. |
| `TranslateLibraryIds_EmptyInput_ReturnsEmpty` | Empty input returns empty. |
| `TranslateLibraryIds_NullInput_ReturnsEmpty` | Null input returns empty without throwing. |
| `ShouldSyncPolicyProperty_IncludesGenericPolicyFields` | Generic policy fields like IsAdministrator/EnabledFolders are syncable. |
| `ShouldSyncPolicyProperty_ExcludesServerSpecificFields` | Server-specific policy fields (channels, devices, etc.) are blocked. |
| `ShouldSyncConfigurationProperty_ExcludesUiSpecificFields` | UI-specific configuration fields are excluded from sync. |
| `RequiresLibraryTranslation_KnownProperties` | Library-ID properties are flagged for translation; others are not. |
| `ComputeMergedPolicy_NullOrEmpty_ReturnsNull` | Null or empty source policy returns null merged policy. |
| `ComputeMergedPolicy_TranslatesEnabledFolders` | EnabledFolders in the merged policy contains local IDs, not source IDs. |
| `ComputeMergedPolicy_PassesThroughNonTranslatedProperties` | Non-translated policy properties pass through unchanged. |
| `ComputeMergedPolicy_TranslatesContentDeletionFolders` | EnableContentDeletionFromFolders also goes through library-ID translation. |
| `ComputeMergedPolicy_InvalidJson_ReturnsOriginal` | Malformed source JSON is passed through as-is rather than throwing. |
| `JsonEquals_DelegatesToJsonComparisonUtility` | JsonEquals on the service delegates to JsonComparisonUtility. |
| `UpdateMergedValue_SetsValueSourceAndRecomputesHash` | UpdateMergedValue stores into Value.Source and recomputes the hash. |
| `MergedValue_SetterDelegatesToValueSource_NoHashSideEffect` | MergedValue setter delegates to Value.Source without touching the hash. |
| `LocalValue_SetterDelegatesToValueLocal` | LocalValue setter delegates to Value.Local. |
| `SourceValueHash_SetterDelegatesToValueSourceHash` | SourceValueHash setter delegates to Value.SourceHash. |
| `SyncedValueHash_SetterDelegatesToValueSyncedHash` | SyncedValueHash setter delegates to Value.SyncedHash. |
| `HasChanges_Policy_ShortCircuitsOnHashMatch` | Policy HasChanges short-circuits on hash match. |
| `HasChanges_Policy_DetectsRealDiff_AfterSourceMoves` | Source moves + LocalValue diverges → HasChanges fires. |
| `HasChanges_Policy_FreshRow_NoChanges_IsFalse` | Fresh Policy row where source matches local returns no changes. |
| `HasChanges_Configuration_DetectsDiff` | Configuration HasChanges detects a real merged-vs-local diff. |
| `HasChanges_ProfileImage_HashMatch_IsFalse` | ProfileImage HasChanges is false when hashes match. |
| `HasChanges_ProfileImage_HashDiffers_IsTrue` | ProfileImage HasChanges is true when hashes differ. |
| `HasChanges_ProfileImage_SourceRemovedImage_LocalStillHas_IsTrue` | Source cleared the image but local still has one → HasChanges fires. |
| `HasChanges_ProfileImage_NeitherHasImage_IsFalse` | Neither side has a profile image → no changes. |
| `HasChanges_ProfileImage_NoSourceHash_SizeFallback_Differs` | No SourceImageHash but different sizes still trigger a sync. |
| `HasChanges_ProfileImage_NoSourceHash_SizeFallback_Matches` | No SourceImageHash but matching sizes mark the row as Synced. |
| `MarkSynced_Policy_DelegatesToValueMarkSynced` | Policy MarkSynced delegates to Value.MarkSynced. |
| `MarkSynced_Configuration_DelegatesToValueMarkSynced` | Configuration MarkSynced delegates to Value.MarkSynced. |
| `MarkSynced_ProfileImage_CopiesImageHashAndSize` | ProfileImage MarkSynced copies SourceImage* into SyncedImage*. |
| `MarkSynced_ProfileImage_DoesNotTouchValueHash` | ProfileImage MarkSynced does not touch the SyncableValue's SyncedHash. |
| `ChangesSummary_NoChanges_ReturnsNoChanges` | ChangesSummary returns "No changes" for a no-diff row. |
| `ChangesSummary_ProfileImage_FormatsSize` | ProfileImage ChangesSummary formats the source image size. |

### Database

| Test Name | Summary |
| --- | --- |
| `CurrentSchemaVersion_IsTwentyOne` | Pins the current schema version constant. |
| `GetSchemaVersion_ReadsPragmaUserVersion` | GetSchemaVersion reads SQLite PRAGMA user_version. |
| `SetSchemaVersion_WritesPragmaUserVersion` | SetSchemaVersion writes SQLite PRAGMA user_version. |
| `CreateInitialSchema_CreatesAllSyncTables` | CreateInitialSchema produces every sync table for fresh installs. |
| `CreateInitialSchema_HistoryTable_HasSourceStateHashColumns` | Fresh-install HistorySyncItems has the v21 SyncableValue hash columns. |
| `CreateInitialSchema_UserTable_HasSourceValueHashColumns` | Fresh-install UserSyncItems has the SourceValueHash/SyncedValueHash columns. |
| `MigrateSchema_FromV18_HardResetsAndRecreatesAllTables` | fromVersion=18 drops old tables and recreates the canonical schema. |
| `MigrateSchema_FromV18_CreatesAllExpectedTables` | Migrating from v18 (hard reset) leaves all expected tables in place. |
| `MigrateSchema_FromV19_ClearsMetadataSyncedHashAndAltersHistory` | v19 → v21 runs both the v20 clear and v21 ALTER steps. |
| `MigrateSchema_FromV20_DoesNotClearMetadataSyncedHashAgain` | v20 → v21 preserves Metadata SyncedHashes but still ALTERs History. |
| `MigrateSchema_FromV20_ClearsUserSyncedValueHash` | v20 → v21 also clears UserSync SyncedValueHash (hash format change). |
| `MigrateSchema_FromV20_Idempotent_IfColumnsAlreadyExist` | Running the v21 ALTER twice (column already exists) returns success. |
| `MigrateSchema_FromV21_NoOp_StillReturnsOk` | fromVersion=21 (already current) returns success without doing migration work. |

### Utilities

| Test Name | Summary |
| --- | --- |
| `TranslatePath_RootsWithTrailingSlash_AreNormalised` | Trailing slashes on either root are normalised before translation. |
| `TranslatePath_BasicTranslation_MapsRelativeTail` | A source path under sourceRoot maps to localRoot + relative tail. |
| `TranslatePath_NullOrEmptySource_ReturnsLocalRoot` | Null or empty source path returns localRoot unchanged. |
| `TranslatePath_SourceNotUnderRoot_FallsBackToLocalRootPlusFilename` | Source path not under sourceRoot falls back to localRoot + filename. |
| `TranslatePath_BlocksDotDotTraversal` | Path-traversal segments (..) are stripped from the translated path. |
| `TranslatePath_StripsSingleDotSegments` | Single-dot segments (.) are stripped from the translated path. |
| `TranslatePath_SourceEqualsRoot_ReturnsLocalRoot` | Source path equal to sourceRoot returns localRoot. |
| `TranslatePath_PrefixMatch_IsCaseInsensitive` | Case-insensitive prefix match (Windows-friendly behaviour). |
| `IsItemFiltered_AllowAll_NeverFilters` | AllowAll mode never filters anything. |
| `IsItemFiltered_Whitelist_MatchingItem_IsKept` | Whitelist with a matching item returns false (keep, don't skip). |
| `IsItemFiltered_Whitelist_NonMatchingItem_IsFiltered` | Whitelist with a non-matching item returns true (skip). |
| `IsItemFiltered_Blacklist_MatchingItem_IsFiltered` | Blacklist with a matching item returns true (skip). |
| `IsItemFiltered_Blacklist_NonMatchingItem_IsKept` | Blacklist with a non-matching item returns false (keep). |
| `IsItemFiltered_EmptyFilteredItems_IsKept` | Empty FilteredItems list short-circuits to "no filter." |
| `IsItemFiltered_NullFilteredItems_IsKept` | Null FilteredItems list short-circuits to "no filter." |
| `IsItemFiltered_EmptySourcePath_Whitelist_IsFiltered` | Empty source path under Whitelist mode is filtered. |
| `IsItemFiltered_EmptySourcePath_Blacklist_IsKept` | Empty source path under Blacklist mode is kept. |
| `IsItemFiltered_ChildOfFilteredFolder_InheritsOutcome` | Child paths inherit their parent's filter outcome. |
| `IsItemFiltered_PrefixWithoutSegmentBoundary_DoesNotMatch` | "Foobar" doesn't match a "Foo" filter entry. |
| `IsItemFiltered_RelativeFilterPath_MatchesAfterPrefixing` | Relative filter paths are prefixed with sourceRoot before matching. |
| `NormalizeStringArray_Null_ReturnsNull` | Null input returns null. |
| `NormalizeStringArray_Empty_ReturnsNull` | Empty array returns null. |
| `NormalizeStringArray_OnlyWhitespace_ReturnsNull` | Array of only whitespace/empty strings returns null. |
| `NormalizeStringArray_DropsWhitespaceEntries` | Mixed whitespace and real values drops the whitespace. |
| `NormalizeStringArray_SortsCaseInsensitively` | Values are sorted case-insensitively. |
| `NormalizeStringArray_SingleValue_Survives` | Single value passes through. |
| `UnwrapKiotaPrimitive_Null_ReturnsNull` | Null input returns null. |
| `UnwrapKiotaPrimitive_PlainString_PassesThrough` | Plain string input passes through unchanged. |
| `UnwrapKiotaPrimitive_UntypedString_ReturnsValue` | UntypedString returns its underlying string value. |
| `UnwrapKiotaPrimitive_UntypedBoolean_ReturnsLowercaseString` | UntypedBoolean returns "true"/"false" lowercase. |
| `UnwrapKiotaPrimitive_UntypedInteger_ReturnsInvariantString` | UntypedInteger uses InvariantCulture formatting. |
| `UnwrapKiotaPrimitive_UntypedLong_ReturnsInvariantString` | UntypedLong uses InvariantCulture formatting. |
| `UnwrapKiotaPrimitive_UntypedDouble_ReturnsInvariantString` | UntypedDouble uses InvariantCulture formatting (dots, not commas). |
| `UnwrapKiotaPrimitive_UntypedNull_ReturnsNull` | UntypedNull yields null. |
| `UnwrapKiotaPrimitive_PlainCLRType_FallsBackToToString` | Non-Kiota CLR types fall back to .ToString(). |
| `GetItemSize_FromFirstMediaSource_ReturnsValue` | GetItemSize reads Size from the first media source. |
| `GetItemSize_NoMediaSources_ReturnsZero` | GetItemSize returns 0 for an item without MediaSources. |
| `GetItemSize_NullSize_ReturnsZero` | GetItemSize returns 0 when the first media source has null Size. |
| `AssignString_PresentValue_InvokesAssignWithValue` | AssignString invokes the callback with the parsed value when present. |
| `AssignString_AbsentKey_DoesNotInvokeAssign` | AssignString does not invoke the callback when the key is absent. |
| `AssignString_NullValue_InvokesAssignWithNull` | AssignString invokes the callback with null when the key is present-and-null. |
| `AssignInt_PresentValue_InvokesAssignWithValue` | AssignInt invokes the callback with the parsed value when present. |
| `AssignInt_AbsentKey_DoesNotInvoke` | AssignInt does not invoke the callback when the key is absent. |
| `AssignInt_NullValue_InvokesWithNull` | AssignInt invokes with null when the value is JSON null. |
| `AssignFloat_PresentValue_InvokesAssignWithValue` | AssignFloat invokes the callback with the parsed value when present. |
| `AssignFloat_NullValue_InvokesWithNull` | AssignFloat invokes with null when value is JSON null. |
| `ParseNullableDate_IsoString_Parses` | ParseNullableDate parses ISO 8601 strings with RoundtripKind. |
| `ParseNullableDate_NonString_ReturnsNull` | ParseNullableDate returns null on non-string JSON kinds. |
| `ParseNullableDate_EmptyString_ReturnsNull` | ParseNullableDate returns null when the string is empty. |
| `ParseNullableDate_Unparseable_ReturnsNull` | ParseNullableDate returns null on unparseable input. |
| `ReadStringArray_ArrayInput_ReturnsArray` | ReadStringArray returns an array for JSON array input. |
| `ReadStringArray_NonArrayInput_ReturnsEmpty` | ReadStringArray returns empty for non-array input. |
| `ReadStringArray_MixedTypes_KeepsOnlyStrings` | ReadStringArray drops non-string entries. |
| `ReadEnumArray_StringValues_ParsesToEnum` | ReadEnumArray parses string values into enum members. |
| `ReadEnumArray_UnparseableEntries_AreDropped` | ReadEnumArray drops unparseable entries. |
| `ReadProviderIds_StringValues_AreKept` | ReadProviderIds reads case-insensitive keys with string values. |
| `ReadProviderIds_EmptyValues_AreDropped` | ReadProviderIds drops empty-string values. |
| `ReadProviderIds_NonObject_ReturnsEmpty` | ReadProviderIds returns empty for non-object input. |

### SmokeTest

| Test Name | Summary |
| --- | --- |
| `TestProjectIsWiredUp` | Discovery smoke test for the test project itself. |

---

# AI Disclaimer

Claude Code was utilized for to build testing to ensure that future releases would have an automated method for validating for breaking changes.