namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What to do with an incoming value, given the stamps on both sides.</summary>
public enum VersionDecision
{
    /// <summary>The values already match. Write nothing and adopt the incoming stamp.</summary>
    Adopt,

    /// <summary>The incoming edit is newer. Write it and carry its stamp.</summary>
    Apply,

    /// <summary>The local edit is newer. Keep it, and tell the peers about it.</summary>
    Keep
}
