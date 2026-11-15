namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Outcome of a paginated fetch loop.
/// <see cref="CompletedFully"/> is false when the loop broke early on
/// errors, null results, or cancellation. Callers that prune what they did
/// not see this run must treat partial discovery as unsafe to prune, since
/// the unseen items may just be ones the loop never reached.
/// </summary>
/// <param name="ProcessedItems">How many items the loop handled.</param>
/// <param name="CompletedFully">Whether the loop reached the end of the list.</param>
public readonly record struct PaginatedFetchOutcome(int ProcessedItems, bool CompletedFully);
