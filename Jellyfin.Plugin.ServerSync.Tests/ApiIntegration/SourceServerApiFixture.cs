using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ApiIntegration;

/// <summary>
/// Shared, lazily initialized state for the API integration tests: one
/// <see cref="SourceServerClient"/> plus a discovery pass that locates a
/// library with leaf items, a sample item page, and the user list. Created
/// only when the first non-skipped test in the collection actually runs, so
/// it never touches the network when the environment variables are absent.
/// </summary>
public sealed class SourceServerApiFixture : IAsyncLifetime
{
    public HttpClient HttpClient { get; private set; } = null!;

    public SourceServerClient Client { get; private set; } = null!;

    public List<VirtualFolderInfo> Libraries { get; private set; } = new();

    /// <summary>First mapped-style library that actually contains leaf items, or null.</summary>
    public VirtualFolderInfo? LibraryWithItems { get; private set; }

    public Guid LibraryWithItemsId { get; private set; }

    /// <summary>First page (up to 100) of leaf items from <see cref="LibraryWithItems"/>.</summary>
    public List<BaseItemDto> SampleItems { get; private set; } = new();

    public List<UserDto> Users { get; private set; } = new();

    public async Task InitializeAsync()
    {
        // xunit creates collection fixtures even when every test in the
        // collection is skipped, so a missing environment must be a no-op
        // here rather than an initialization failure.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiFactAttribute.UrlVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiFactAttribute.KeyVariable)))
        {
            return;
        }

        HttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Client = new SourceServerClient(
            NullLogger<SourceServerClient>.Instance,
            HttpClient,
            ApiFactAttribute.ServerUrl,
            ApiFactAttribute.ApiKey,
            "ServerSync API Tests",
            "0.0.0.0");

        Libraries = await Client.GetLibrariesAsync();
        Users = await Client.GetUsersAsync();

        foreach (var lib in Libraries)
        {
            if (string.IsNullOrEmpty(lib.ItemId) || !Guid.TryParse(lib.ItemId, out var libId))
            {
                continue;
            }

            var page = await Client.GetLibraryItemsAsync(libId, startIndex: 0, limit: 100);
            if (page?.Items is { Count: > 0 })
            {
                LibraryWithItems = lib;
                LibraryWithItemsId = libId;
                SampleItems = page.Items.Where(i => i.Id.HasValue && !string.IsNullOrEmpty(i.Path)).ToList();
                break;
            }
        }
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        HttpClient?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a raw authenticated GET to the test server, bypassing
    /// <see cref="SourceServerClient"/>. Used by parity tests that compare
    /// the client's behavior against the wire format.
    /// </summary>
    public async Task<HttpResponseMessage> RawGetAsync(string pathAndQuery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiFactAttribute.ServerUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "MediaBrowser",
            $"Client=\"Server Sync\", Device=\"ServerSync API Tests\", DeviceId=\"serversync-api-tests\", Version=\"0.0.0.0\", Token=\"{ApiFactAttribute.ApiKey}\"");
        return await HttpClient.SendAsync(request);
    }
}

[CollectionDefinition("SourceServerApi")]
public class SourceServerApiCollection : ICollectionFixture<SourceServerApiFixture>
{
}
