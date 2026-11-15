using System;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for the client factory in tests that never reach a source. A call to build a client
/// fails loudly and is counted.
/// </summary>
public sealed class FakeClientFactory : ISourceServerClientFactory
{
    /// <summary>
    /// Gets how many clients were asked for.
    /// </summary>
    public int Requests { get; private set; }

    /// <inheritdoc/>
    public SourceServerClient Create(SourceServer server)
    {
        Requests++;
        throw new NotSupportedException("This test never builds a client.");
    }
}
