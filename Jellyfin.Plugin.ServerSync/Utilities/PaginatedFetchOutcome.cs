namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// The outcome of a paginated fetch loop.
/// A loop that did not complete must not be pruned against, since unseen items may only be ones it never reached.
/// </summary>
/// <param name="ProcessedItems">How many items the loop handled.</param>
/// <param name="CompletedFully">Whether the loop reached the end of the list, which is <c>false</c> after errors, null pages, or a cancel.</param>
public readonly record struct PaginatedFetchOutcome(int ProcessedItems, bool CompletedFully);
