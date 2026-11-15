using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Groups the queue tests that touch process-wide state, such as the loop breaker, the apply guard, the provider fills, and the playlist copy registry, so they run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QueueStateCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "QueueState";
}
