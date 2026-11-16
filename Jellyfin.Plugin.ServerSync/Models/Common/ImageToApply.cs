namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// One image an apply brings over from a source.
/// </summary>
/// <param name="ImageType">The image type's name, such as Primary or Backdrop.</param>
/// <param name="SourceIndex">The image's index on the source, or null for a type's only image.</param>
/// <param name="TargetIndex">The slot the image is saved to on this server.</param>
public readonly record struct ImageToApply(string ImageType, int? SourceIndex, int TargetIndex);
