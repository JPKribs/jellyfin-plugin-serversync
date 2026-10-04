namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// What kind of object a hint describes. Each kind has its own key shape and its own apply code on
/// the receiver. Only history travels as a hint today. The other kinds arrive in later phases.
/// </summary>
public enum HintKind
{
    /// <summary>Watch history for one user and one item. The key is the origin's user id and item id.</summary>
    History = 0,

    /// <summary>Metadata, images, people, and studios of one library item. The key is the origin's item id.</summary>
    Metadata = 1,

    /// <summary>A person's own metadata and images. The key is the person's name.</summary>
    People = 2,

    /// <summary>A media file that appeared on the origin. The key is the origin's item id.</summary>
    Content = 3,

    /// <summary>A user's policy, configuration, and profile image. The key is the origin's user id.</summary>
    Users = 4
}
