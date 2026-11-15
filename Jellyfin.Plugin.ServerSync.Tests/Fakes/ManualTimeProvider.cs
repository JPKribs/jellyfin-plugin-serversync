using System;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A clock a test moves by hand, so a wait passes without real time.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far to move it.</param>
    public void Advance(TimeSpan by) => _now += by;
}
