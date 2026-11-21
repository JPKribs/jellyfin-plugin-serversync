using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A real hint activity log over a recording stub of Jellyfin's activity manager, so a test can read the entries written.
/// </summary>
public sealed class HintActivityHarness : IDisposable
{
    private readonly InterfaceStub _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintActivityHarness"/> class.
    /// </summary>
    public HintActivityHarness()
    {
        var (manager, stub) = InterfaceStub.Create<IActivityManager>();
        stub.Answer(nameof(IActivityManager.CreateAsync), _ => Task.CompletedTask);
        _manager = stub;
        Logger = new ActivityLogger(manager, NullLogger<ActivityLogger>.Instance);
        Log = new HintActivityLog(Logger);
    }

    /// <summary>Gets the activity writer the log is built on.</summary>
    public ActivityLogger Logger { get; }

    /// <summary>Gets the hint activity log.</summary>
    public HintActivityLog Log { get; }

    /// <summary>Gets the types of the entries written so far, in order.</summary>
    public List<string> Types => _manager.Calls
        .Where(call => call.Name == nameof(IActivityManager.CreateAsync))
        .Select(call => ((ActivityLog)call.Args[0]!).Type)
        .ToList();

    /// <summary>Gets the names and overviews of the entries written so far, in order.</summary>
    public List<(string Name, string Overview)> Entries => _manager.Calls
        .Where(call => call.Name == nameof(IActivityManager.CreateAsync))
        .Select(call => (ActivityLog)call.Args[0]!)
        .Select(entry => (entry.Name, entry.ShortOverview ?? string.Empty))
        .ToList();

    /// <inheritdoc/>
    public void Dispose() => Log.Dispose();
}
