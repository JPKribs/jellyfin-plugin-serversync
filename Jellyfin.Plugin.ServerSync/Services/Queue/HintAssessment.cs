using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What the receiver decided about a hint as it arrived.</summary>
/// <param name="Code">The code the sender is told.</param>
/// <param name="Reason">Why, when the hint is not simply queued.</param>
/// <param name="ParkKey">The key a hint waits under until its local item or person appears.</param>
public readonly record struct HintAssessment(HintCode Code, string? Reason, string? ParkKey = null)
{
    /// <summary>Gets a value indicating whether the hint is stored, to apply now or once its local object appears.</summary>
    public bool Keep => Code is HintCode.Queued or HintCode.NoLocalItem;
}
