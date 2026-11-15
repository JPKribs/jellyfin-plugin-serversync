using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Groups the tests that measure real time or real disk space, so they run alone and a busy test run cannot stretch their waits.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTimeCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "RealTime";
}
