using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// What replacing an item's images did.
/// </summary>
/// <param name="Changed">Whether an image was removed or saved, so the item needs saving.</param>
/// <param name="Attempted">How many images the apply was asked to bring over.</param>
/// <param name="Saved">How many images were saved.</param>
/// <param name="DownloadFailures">How many images could not be downloaded.</param>
/// <param name="SaveFailures">How many downloaded images could not be saved.</param>
/// <param name="Errors">One line for each image type that did not fully land.</param>
public sealed record ImageApplyResult(bool Changed, int Attempted, int Saved, int DownloadFailures, int SaveFailures, IReadOnlyList<string> Errors);
