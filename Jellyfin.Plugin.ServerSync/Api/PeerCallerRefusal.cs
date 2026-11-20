namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The body of a 403 from <see cref="PeerCallerGuard"/>.
/// </summary>
public sealed class PeerCallerRefusal
{
    /// <summary>Gets or sets the refusal's code, which names why the caller was refused.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the reason in words.</summary>
    public string Message { get; set; } = string.Empty;
}
