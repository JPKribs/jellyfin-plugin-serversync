using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The provider must open the one shared database on first use, hand the same one to every caller, and close it when disposed.
/// </summary>
public sealed class SyncDatabaseProviderTests : IDisposable
{
    private readonly DatabaseTempFolder _folder = new("provider");

    /// <inheritdoc/>
    public void Dispose() => _folder.Dispose();

    /// <summary>
    /// The constructor refuses a missing logger factory or paths.
    /// True: a bad registration fails when Jellyfin builds its services.
    /// False: the first store to ask for the database would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SyncDatabaseProvider(null!, Paths()));
        Assert.Throws<ArgumentNullException>(() => new SyncDatabaseProvider(NullLoggerFactory.Instance, null!));
    }

    /// <summary>
    /// Building the provider opens nothing, and the first read of the database creates it under the data folder.
    /// True: Jellyfin builds its services even when the database cannot be opened yet.
    /// False: a database fault would stop Jellyfin from starting.
    /// </summary>
    [Fact]
    public void Database_FirstRead_OpensItLazily()
    {
        using var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, Paths());
        var existedBefore = File.Exists(_folder.DatabaseFile);

        var database = provider.Database;

        Assert.False(existedBefore);
        Assert.NotNull(database);
        Assert.True(File.Exists(_folder.DatabaseFile));
    }

    /// <summary>
    /// Every read returns the same database.
    /// True: every store shares one write connection and one write lock.
    /// False: two stores would write through two connections and block each other.
    /// </summary>
    [Fact]
    public void Database_ReadTwice_ReturnsTheSameInstance()
    {
        using var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, Paths());

        Assert.Same(provider.Database, provider.Database);
    }

    /// <summary>
    /// Disposing the provider closes the database it opened, and later reads are refused.
    /// True: the file is released at shutdown and nothing reopens it.
    /// False: a late caller would open a second database after shutdown.
    /// </summary>
    [Fact]
    public void Dispose_AfterOpening_ClosesTheDatabaseAndRefusesReads()
    {
        var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, Paths());
        var database = provider.Database;

        provider.Dispose();
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => provider.Database);
        Assert.Throws<ObjectDisposedException>(() => database.ResetDatabase());
    }

    /// <summary>
    /// A provider disposed before any use never creates a database.
    /// True: a server that never syncs leaves no file behind.
    /// False: shutdown would create the database it then closes.
    /// </summary>
    [Fact]
    public void Dispose_NeverUsed_CreatesNothing()
    {
        var provider = new SyncDatabaseProvider(NullLoggerFactory.Instance, Paths());

        provider.Dispose();

        Assert.False(File.Exists(_folder.DatabaseFile));
        Assert.Throws<ObjectDisposedException>(() => provider.Database);
    }

    private IApplicationPaths Paths()
    {
        var (paths, stub) = InterfaceStub.Create<IApplicationPaths>();
        stub.Answer("get_" + nameof(IApplicationPaths.DataPath), _ => _folder.DataPath);
        return paths;
    }
}
