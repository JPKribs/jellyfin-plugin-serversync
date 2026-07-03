using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.ApiIntegration;

/// <summary>
/// Read-only integration tests that exercise every <see cref="SourceServerClient"/>
/// GET path against a live Jellyfin server. Skipped unless
/// SERVERSYNC_TEST_SERVER_URL and SERVERSYNC_TEST_API_KEY are set — see
/// <see cref="ApiFactAttribute"/>. These tests never write to the server.
/// </summary>
[Collection("SourceServerApi")]
public class SourceServerClientApiTests
{
    private readonly SourceServerApiFixture _fx;
    private readonly ITestOutputHelper _output;

    public SourceServerClientApiTests(SourceServerApiFixture fixture, ITestOutputHelper output)
    {
        _fx = fixture;
        _output = output;
    }

    // =====================================================================
    // Connection
    // =====================================================================

    [ApiFact]
    public async Task TestConnection_Succeeds()
    {
        var result = await _fx.Client.TestConnectionAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(string.IsNullOrEmpty(result.ServerName));
        Assert.False(string.IsNullOrEmpty(result.ServerId));
    }

    [ApiFact]
    public async Task TestConnection_WithInvalidKey_Fails()
    {
        // Regression: the connection test must NOT fall back to the anonymous
        // /System/Info/Public endpoint on a 401 — a bad key must fail loudly.
        using var badClient = new SourceServerClient(
            NullLogger<SourceServerClient>.Instance,
            _fx.HttpClient,
            ApiFactAttribute.ServerUrl,
            "00000000000000000000000000000000",
            "ServerSync API Tests",
            "0.0.0.0");

        var result = await badClient.TestConnectionAsync();

        Assert.False(result.Success, "An invalid API key must not report a successful connection");
    }

    // =====================================================================
    // Libraries and users
    // =====================================================================

    [ApiFact]
    public void GetLibraries_ReturnsNamedLibraries()
    {
        Assert.NotEmpty(_fx.Libraries);
        Assert.All(_fx.Libraries, lib =>
        {
            Assert.False(string.IsNullOrEmpty(lib.ItemId));
            Assert.False(string.IsNullOrEmpty(lib.Name));
        });
    }

    [ApiFact]
    public void GetUsers_ReturnsUsersWithIds()
    {
        Assert.NotEmpty(_fx.Users);
        Assert.All(_fx.Users, u =>
        {
            Assert.NotNull(u.Id);
            Assert.False(string.IsNullOrEmpty(u.Name));
        });
    }

    // =====================================================================
    // Library item enumeration (Content / Metadata / History discovery)
    // =====================================================================

    [ApiFact]
    public async Task GetLibraryItems_ReturnsLeafItemsWithPathAndMediaSources()
    {
        Assert.NotNull(_fx.LibraryWithItems);
        Assert.NotEmpty(_fx.SampleItems);
        Assert.All(_fx.SampleItems, item =>
        {
            Assert.NotNull(item.Id);
            Assert.False(string.IsNullOrEmpty(item.Path));
            Assert.NotNull(item.MediaSources);
        });

        // Pagination advances: when the library has more items than one page,
        // the second page must not repeat the first page's leading item.
        var first = await _fx.Client.GetLibraryItemsAsync(_fx.LibraryWithItemsId, startIndex: 0, limit: 5);
        Assert.NotNull(first?.Items);
        if (first!.TotalRecordCount > 5)
        {
            var second = await _fx.Client.GetLibraryItemsAsync(_fx.LibraryWithItemsId, startIndex: 5, limit: 5);
            Assert.NotNull(second?.Items);
            Assert.NotEqual(first.Items![0].Id, second!.Items![0].Id);
        }
    }

    [ApiFact]
    public async Task GetLibraryItemCount_MatchesTotalRecordCount()
    {
        Assert.NotNull(_fx.LibraryWithItems);

        var count = await _fx.Client.GetLibraryItemCountAsync(_fx.LibraryWithItemsId);
        var page = await _fx.Client.GetLibraryItemsAsync(_fx.LibraryWithItemsId, startIndex: 0, limit: 1);

        Assert.True(count > 0);
        Assert.Equal(page?.TotalRecordCount, count);
    }

    [ApiFact]
    public async Task GetLibraryItemPaths_ReturnsIdAndPathForEveryItem()
    {
        Assert.NotNull(_fx.LibraryWithItems);

        var leafTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
        var page = await _fx.Client.GetLibraryItemPathsAsync(_fx.LibraryWithItemsId, leafTypes, startIndex: 0, limit: 1000);

        Assert.NotNull(page?.Items);
        Assert.NotEmpty(page!.Items!);
        Assert.All(page.Items!, item =>
        {
            Assert.NotNull(item.Id);
            Assert.False(string.IsNullOrEmpty(item.Path));
        });
    }

    [ApiFact]
    public async Task GetItemsByIds_ReturnsEveryRequestedItemWithMetadataFields()
    {
        Assert.NotEmpty(_fx.SampleItems);

        var requested = _fx.SampleItems.Take(50).Select(i => i.Id!.Value).ToList();
        var fields = new[]
        {
            ItemFields.Path, ItemFields.DateCreated, ItemFields.ProviderIds, ItemFields.Overview,
            ItemFields.Genres, ItemFields.Tags, ItemFields.Studios, ItemFields.People, ItemFields.Settings
        };

        var items = await _fx.Client.GetItemsByIdsAsync(requested, fields);

        Assert.Equal(requested.Count, items.Count);
        Assert.Equal(
            requested.OrderBy(g => g),
            items.Select(i => i.Id!.Value).OrderBy(g => g));

        // Requested list-fields come back materialized (possibly empty), not null.
        Assert.All(items, i =>
        {
            Assert.NotNull(i.Genres);
            Assert.NotNull(i.Tags);
            Assert.NotNull(i.LockData);
        });
    }

    [ApiFact]
    public async Task GetItemDetails_ReturnsItemWithPath()
    {
        Assert.NotEmpty(_fx.SampleItems);

        var details = await _fx.Client.GetItemDetailsAsync(_fx.SampleItems[0].Id!.Value);

        Assert.NotNull(details);
        Assert.Equal(_fx.SampleItems[0].Id, details!.Id);
        Assert.False(string.IsNullOrEmpty(details.Path));
    }

    // =====================================================================
    // Persons — the /Items?includeItemTypes=Person pagination must be a
    // faithful (or better) replacement for the unpaginatable /Persons
    // endpoint: nothing missing, no duplicates, identical field payloads.
    // =====================================================================

    [ApiFact]
    public async Task GetAllPersons_PaginatedFetch_IsFaithfulSupersetOfPersonsEndpoint()
    {
        var persons = await _fx.Client.GetAllPersonsAsync();

        if (persons.Count == 0)
        {
            _output.WriteLine("Server has no persons — nothing to compare.");
            return;
        }

        // No duplicate IDs across page boundaries.
        var clientIds = new HashSet<Guid>();
        foreach (var p in persons)
        {
            Assert.NotNull(p.Id);
            Assert.True(clientIds.Add(p.Id!.Value), $"Duplicate person {p.Id} returned by paginated fetch");
        }

        // Wire-format comparison: hash every person from the raw /Persons
        // endpoint and from the raw paginated /Items endpoint, then require
        // /Items ⊇ /Persons with byte-identical canonical JSON per person.
        const string fields = "Overview,ProviderIds,Tags,OriginalTitle,SortName,DateCreated,ProductionLocations,Settings";
        var personsEndpoint = await HashItemsAsync($"/Persons?fields={fields}");
        var itemsEndpoint = new Dictionary<string, string>();
        var startIndex = 0;
        while (true)
        {
            var (page, total) = await HashItemsPageAsync(
                $"/Items?includeItemTypes=Person&recursive=true&fields={fields}&sortBy=SortName&startIndex={startIndex}&limit=1000");
            if (page.Count == 0)
            {
                break;
            }

            foreach (var kvp in page)
            {
                itemsEndpoint[kvp.Key] = kvp.Value;
            }

            startIndex += page.Count;
            if (startIndex >= total)
            {
                break;
            }
        }

        _output.WriteLine($"/Persons unique: {personsEndpoint.Count}; /Items Person unique: {itemsEndpoint.Count}; client returned: {persons.Count}");

        var missing = personsEndpoint.Keys.Where(id => !itemsEndpoint.ContainsKey(id)).Take(5).ToList();
        Assert.True(missing.Count == 0, $"Persons missing from paginated /Items fetch: {string.Join(", ", missing)}");

        var mismatched = personsEndpoint.Where(kvp => itemsEndpoint[kvp.Key] != kvp.Value).Take(5).Select(kvp => kvp.Key).ToList();
        Assert.True(mismatched.Count == 0, $"Persons with differing payloads between endpoints: {string.Join(", ", mismatched)}");

        // And the client's own result covers everything /Persons knows about.
        var clientIdSet = clientIds.Select(g => g.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFromClient = personsEndpoint.Keys.Where(id => !clientIdSet.Contains(id)).Take(5).ToList();
        Assert.True(missingFromClient.Count == 0, $"Persons missing from GetAllPersonsAsync: {string.Join(", ", missingFromClient)}");
    }

    // =====================================================================
    // Images (Metadata / People enrichment paths)
    // =====================================================================

    [ApiFact]
    public async Task GetItemImageInfo_And_HeadContentLength_Work()
    {
        var withImage = _fx.SampleItems.FirstOrDefault(i => i.ImageTags?.AdditionalData?.ContainsKey("Primary") == true);
        if (withImage == null)
        {
            _output.WriteLine("No sample item with a Primary image — skipping.");
            return;
        }

        var info = await _fx.Client.GetItemImageInfoAsync(withImage.Id!.Value);
        Assert.NotNull(info);
        Assert.Contains(info!, i => i.ImageType?.ToString() == "Primary");

        var headSize = await _fx.Client.GetItemImageContentLengthAsync(withImage.Id!.Value, "Primary", null);
        Assert.NotNull(headSize);
        Assert.True(headSize > 0, "HEAD on the Primary image must report a positive Content-Length");
    }

    [ApiFact]
    public async Task GetUserImageHashAndSize_ReturnsConsistentPair()
    {
        Assert.NotEmpty(_fx.Users);

        var (hash, size) = await _fx.Client.GetUserImageHashAndSizeAsync(_fx.Users[0].Id!.Value);

        if (hash == null)
        {
            // No profile image (or fetch failed) — both halves must agree.
            Assert.Null(size);
        }
        else
        {
            Assert.Equal(32, hash.Length);
            Assert.True(size > 0);
        }
    }

    // =====================================================================
    // History (user-data paths)
    // =====================================================================

    [ApiFact]
    public async Task GetItemsWithUserDataByIds_PopulatesUserData()
    {
        Assert.NotEmpty(_fx.Users);
        Assert.NotEmpty(_fx.SampleItems);

        var ids = _fx.SampleItems.Take(10).Select(i => (Guid?)i.Id!.Value).ToArray();
        var page = await _fx.Client.GetItemsWithUserDataByIdsAsync(_fx.Users[0].Id!.Value, ids);

        Assert.NotNull(page?.Items);
        Assert.NotEmpty(page!.Items!);
        Assert.All(page.Items!, i => Assert.NotNull(i.UserData));
    }

    [ApiFact]
    public async Task GetUserPlayedItemIds_ReturnsWithoutConflatingErrorAndEmpty()
    {
        Assert.NotEmpty(_fx.Users);
        Assert.NotNull(_fx.LibraryWithItems);

        // Must complete without throwing; an empty set is a legitimate result,
        // a throw means the source gave no real answer.
        var ids = await _fx.Client.GetUserPlayedItemIdsAsync(_fx.Users[0].Id!.Value, _fx.LibraryWithItemsId);

        Assert.NotNull(ids);
        _output.WriteLine($"User {_fx.Users[0].Name} has {ids.Count} played item(s) in {_fx.LibraryWithItems!.Name}.");
    }

    // =====================================================================
    // Downloads (Content sync paths) — read-only: streams are read and
    // discarded, nothing is written to the server.
    // =====================================================================

    [ApiFact]
    public async Task DownloadFile_StreamsRealContent()
    {
        Assert.NotEmpty(_fx.SampleItems);

        // The server may reference files that are currently missing on its
        // disk (unmounted volume, moved media) — that's a server-state 404,
        // not an API regression. Try several items; any non-404 failure or
        // an all-items-missing pass is reported.
        var attempts = 0;
        foreach (var item in _fx.SampleItems.Take(10))
        {
            attempts++;
            (Stream Stream, long? ContentLength)? download;
            try
            {
                download = await _fx.Client.DownloadFileAsync(item.Id!.Value);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("404"))
            {
                _output.WriteLine($"Item {item.Name}: file missing on source (404) — trying next.");
                continue;
            }

            Assert.NotNull(download);
            var (stream, contentLength) = download!.Value;
            await using (stream)
            {
                var buffer = new byte[16 * 1024];
                var read = await stream.ReadAsync(buffer);
                Assert.True(read > 0, "Download stream produced no bytes");
                if (contentLength.HasValue)
                {
                    Assert.True(contentLength.Value > 0);
                }
            }

            _output.WriteLine($"Downloaded first {16 * 1024} bytes of {item.Name} successfully.");
            return;
        }

        _output.WriteLine($"All {attempts} sampled items are missing on the source's disk — download route not exercised on this server.");
    }

    [ApiFact]
    public async Task GetCompanionFiles_SubtitlesOnly_AndDownloadsAreNotVideo()
    {
        // Regression for the 10.11 companion bug: the old download route
        // silently served the item's main video file in place of the
        // subtitle. Find any item with an external subtitle and prove the
        // downloaded bytes are not a video container.
        foreach (var item in _fx.SampleItems.Take(50))
        {
            var companions = await _fx.Client.GetCompanionFilesAsync(item.Id!.Value);
            if (companions.Count == 0)
            {
                continue;
            }

            Assert.All(companions, c =>
            {
                Assert.False(string.IsNullOrEmpty(c.FileName));
                Assert.False(string.IsNullOrEmpty(c.MediaSourceId));
            });

            var companion = companions[0];
            await using var stream = await _fx.Client.DownloadCompanionFileAsync(item.Id!.Value, companion);
            Assert.NotNull(stream);

            using var ms = new MemoryStream();
            await stream!.CopyToAsync(ms);
            var bytes = ms.ToArray();
            Assert.True(bytes.Length > 0, $"Companion {companion.FileName} downloaded 0 bytes");

            // EBML (mkv/webm) magic: 1A 45 DF A3 — the signature of the old
            // corruption where video bytes were written into .srt files.
            var isEbml = bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3;
            Assert.False(isEbml, $"Companion {companion.FileName} contains Matroska video bytes — the corruption regression is back");

            _output.WriteLine($"Companion verified: {companion.FileName} ({bytes.Length} bytes) from item {item.Name}.");
            return;
        }

        _output.WriteLine("No item with external subtitles in the sample — companion download not exercised on this server.");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<Dictionary<string, string>> HashItemsAsync(string pathAndQuery)
    {
        var (hashes, _) = await HashItemsPageAsync(pathAndQuery);
        return hashes;
    }

    /// <summary>
    /// Fetches a raw Items-style payload and returns (Id → SHA-256 of the
    /// item's canonical JSON) plus TotalRecordCount. Canonical = object keys
    /// sorted recursively, so two endpoints serializing the same DTO hash
    /// identically regardless of property order.
    /// </summary>
    private async Task<(Dictionary<string, string> Hashes, int Total)> HashItemsPageAsync(string pathAndQuery)
    {
        using var response = await _fx.RawGetAsync(pathAndQuery);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(body);

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = doc.RootElement.TryGetProperty("TotalRecordCount", out var t) ? t.GetInt32() : 0;
        if (!doc.RootElement.TryGetProperty("Items", out var items))
        {
            return (hashes, total);
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Id", out var idProp) || idProp.GetString() is not { } id)
            {
                continue;
            }

            var sb = new StringBuilder();
            WriteCanonical(item, sb);
            hashes[id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        return (hashes, total);
    }

    private static void WriteCanonical(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(prop.Name)).Append(':');
                    WriteCanonical(prop.Value, sb);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var child in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(child, sb);
                }

                sb.Append(']');
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }
}
