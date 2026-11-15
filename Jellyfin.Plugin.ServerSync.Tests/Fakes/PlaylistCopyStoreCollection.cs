using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// The playlist copy registry is process-wide static state (the store registers itself as the
/// current instance), so every test class that touches it runs in this one serial collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaylistCopyStoreCollection
{
    public const string Name = "PlaylistCopyStore";
}
