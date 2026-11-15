using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The dashboard's shared routes report health, start only the plugin's own tasks, and reset only the table that was named.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class ConfigurationControllerTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Health reads as ready with no message when no plugin instance holds Server Sync.
    /// True: the dashboard shows its pages instead of the held notice.
    /// False: a running plugin would be shown as held with an empty reason.
    /// </summary>
    [Fact]
    public void GetHealth_NotHeld_AnswersReadyWithNoMessage()
    {
        var result = _harness.Configuration().GetHealth();

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(true, body.GetType().GetProperty("Ready")!.GetValue(body));
        Assert.Null(body.GetType().GetProperty("Message")!.GetValue(body));
    }

    /// <summary>
    /// Each of the plugin's ten task keys starts its task at once.
    /// True: the Sync tab's run buttons start the task they name, and no other.
    /// False: a button would do nothing, or start a different task.
    /// </summary>
    [Theory]
    [InlineData("ServerSyncUpdateTables")]
    [InlineData("ServerSyncDownloadContent")]
    [InlineData("ServerSyncRefreshHistoryTable")]
    [InlineData("ServerSyncMissingHistory")]
    [InlineData("ServerSyncRefreshMetadataTable")]
    [InlineData("ServerSyncMissingMetadata")]
    [InlineData("ServerSyncRefreshPeopleTable")]
    [InlineData("ServerSyncMissingPeople")]
    [InlineData("ServerSyncRefreshUserTable")]
    [InlineData("ServerSyncMissingUserData")]
    public void RunTask_PluginTask_StartsThatTask(string key)
    {
        _harness.AddScheduledTask("SomeOtherTask");
        var worker = _harness.AddScheduledTask(key);

        var result = _harness.Configuration().RunTask(key);

        Assert.IsType<OkObjectResult>(result);
        var execute = Assert.Single(_harness.TaskManagerStub.Calls, c => c.Name == nameof(ITaskManager.Execute));
        Assert.Same(worker, execute.Args[0]);
    }

    /// <summary>
    /// A key that is not one of the plugin's tasks is answered 404 and starts nothing, even when Jellyfin has a task by that key.
    /// True: the route cannot be used to start Jellyfin's other scheduled tasks.
    /// False: any task on the server could be started through the plugin's route.
    /// </summary>
    [Theory]
    [InlineData("RefreshLibrary")]
    [InlineData("serversyncupdatetables")]
    [InlineData("ServerSyncUpdateTables ")]
    [InlineData("")]
    [InlineData(null)]
    public void RunTask_KeyOutsideThePluginsTasks_Answers404AndStartsNothing(string? key)
    {
        _harness.AddScheduledTask("RefreshLibrary");
        _harness.AddScheduledTask("serversyncupdatetables");
        _harness.AddScheduledTask("ServerSyncUpdateTables ");
        _harness.AddScheduledTask(string.Empty);

        var result = _harness.Configuration().RunTask(key!);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.DoesNotContain(_harness.TaskManagerStub.Calls, c => c.Name == nameof(ITaskManager.Execute));
    }

    /// <summary>
    /// A plugin task key that Jellyfin does not list is answered 404.
    /// True: a task that failed to register reports as missing.
    /// False: the route would throw, or claim to have started a task that does not exist.
    /// </summary>
    [Fact]
    public void RunTask_PluginTaskNotRegistered_Answers404()
    {
        var result = _harness.Configuration().RunTask("ServerSyncUpdateTables");

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.DoesNotContain(_harness.TaskManagerStub.Calls, c => c.Name == nameof(ITaskManager.Execute));
    }

    /// <summary>
    /// Resetting one table empties that table and leaves the others, whatever the case of its name.
    /// True: only the sync type the operator reset starts over.
    /// False: a reset of one table would wipe another, or a lower case name would be refused.
    /// </summary>
    [Theory]
    [InlineData("Content", 0, 1, 1, 1, 1)]
    [InlineData("content", 0, 1, 1, 1, 1)]
    [InlineData("HISTORY", 1, 0, 1, 1, 1)]
    [InlineData("Metadata", 1, 1, 0, 1, 1)]
    [InlineData("People", 1, 1, 1, 0, 1)]
    [InlineData("Users", 1, 1, 1, 1, 0)]
    [InlineData("Playlists", 1, 1, 1, 1, 1)]
    public void Reset_OneTable_EmptiesOnlyThatTable(string table, int content, int history, int metadata, int people, int users)
    {
        Seed();

        var result = _harness.Configuration(Services()).Reset(table);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(content, _harness.Content.Count());
        Assert.Equal(history, _harness.History.Count());
        Assert.Equal(metadata, _harness.Metadata.Count());
        Assert.Equal(people, _harness.People.Count());
        Assert.Equal(users, _harness.Users.Count());
    }

    /// <summary>
    /// Resetting All recreates the database, so every table is empty and still usable.
    /// True: a full reset clears every sync type and the next scan can write again.
    /// False: rows would survive a full reset, or the database would be left unusable.
    /// </summary>
    [Fact]
    public void Reset_All_EmptiesEveryTableAndLeavesThemUsable()
    {
        Seed();

        var result = _harness.Configuration(Services()).Reset("all");
        _harness.AddContent("after-reset");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, _harness.Content.Count());
        Assert.Equal(0, _harness.History.Count() + _harness.Metadata.Count() + _harness.People.Count() + _harness.Users.Count());
    }

    /// <summary>
    /// A table name the route does not know is answered 404 and resets nothing.
    /// True: a mistyped name is harmless.
    /// False: an unknown name would fall through to a full reset, or throw.
    /// </summary>
    [Theory]
    [InlineData("Everything")]
    [InlineData("Content ")]
    [InlineData("")]
    [InlineData(null)]
    public void Reset_UnknownTable_Answers404AndResetsNothing(string? table)
    {
        Seed();

        var result = _harness.Configuration(Services()).Reset(table!);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(1, _harness.Content.Count());
        Assert.Equal(1, _harness.History.Count());
    }

    /// <summary>
    /// A reset that fails is answered 500 with a body that says it did not work.
    /// True: the dashboard shows an error and points to the log.
    /// False: a failed reset would look like success, or surface as an unhandled error page.
    /// </summary>
    [Fact]
    public void Reset_TableServiceMissing_Answers500()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();

        var result = _harness.Configuration(empty).Reset("Content");

        var failure = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, failure.StatusCode);
        Assert.Equal(false, failure.Value!.GetType().GetProperty("Success")!.GetValue(failure.Value));
    }

    /// <summary>
    /// The controller refuses to be built without its parts.
    /// True: a missing service is named when the controller is built.
    /// False: the first route to use it would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        var h = _harness;
        var logger = NullLogger<ConfigurationController>.Instance;

        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(null!, h.TaskManager, h.HttpClients, h.Config, h.Provider, h.Clients, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, null!, h.HttpClients, h.Config, h.Provider, h.Clients, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, h.TaskManager, null!, h.Config, h.Provider, h.Clients, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, h.TaskManager, h.HttpClients, null!, h.Provider, h.Clients, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, h.TaskManager, h.HttpClients, h.Config, null!, h.Clients, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, h.TaskManager, h.HttpClients, h.Config, h.Provider, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationController(h.Library, h.TaskManager, h.HttpClients, h.Config, h.Provider, h.Clients, null!));
    }

    /// <summary>
    /// Each shared route requires elevation and answers one verb at its own path under Sync.
    /// True: only an administrator can read health, start a task, or reset a table.
    /// False: a signed in user without elevation could reset the sync database.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetHealth), "GET", "Health")]
    [InlineData(nameof(ConfigurationController.RunTask), "POST", "Tasks/{taskKey}/Run")]
    [InlineData(nameof(ConfigurationController.Reset), "POST", "Reset/{table}")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
        Assert.Equal("Sync", Assert.Single(typeof(ConfigurationController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
    }

    private ServiceProvider Services() => _harness.Services(s => s.AddSingleton(_harness.PlaylistCopies));

    private void Seed()
    {
        _harness.AddContent("item");
        _harness.History.Upsert(new HistorySyncItem { SourceUserId = "user", LocalUserId = "local", SourceItemId = "item", SourceLibraryId = "lib", LocalLibraryId = "local", StatusDate = DateTime.UtcNow, ServerKey = "peer" });
        _harness.Metadata.Upsert(new MetadataSyncItem { SourceLibraryId = "lib", LocalLibraryId = "local", SourceItemId = "item", StatusDate = DateTime.UtcNow, ServerKey = "peer" });
        _harness.People.Upsert(new PeopleSyncItem { PersonName = "Person", StatusDate = DateTime.UtcNow, ServerKey = "peer" });
        _harness.Users.Upsert(new UserSyncItem { SourceUserId = "user", LocalUserId = "local", PropertyCategory = UserPropertyCategory.Policy, StatusDate = DateTime.UtcNow, ServerKey = "peer" });
    }
}
