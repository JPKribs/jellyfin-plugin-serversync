using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using JPKribs.Jellyfin.Base;

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
