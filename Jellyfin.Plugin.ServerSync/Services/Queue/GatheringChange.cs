using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>One change still gathering before it becomes a hint.</summary>
/// <param name="Kind">The sync type the change belongs to.</param>
/// <param name="UserId">The local user, for history.</param>
/// <param name="ItemId">The local item.</param>
/// <param name="EditedAt">When it was last edited, in UTC.</param>
/// <param name="Due">When it is sent unless edited again, in UTC.</param>
/// <param name="Recorded">Whether a hand made edit is among the gathered changes.</param>
public sealed record GatheringChange(HintKind Kind, Guid UserId, Guid ItemId, DateTime EditedAt, DateTime Due, bool Recorded);
