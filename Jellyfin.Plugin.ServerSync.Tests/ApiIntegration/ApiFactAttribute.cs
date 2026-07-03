using System;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ApiIntegration;

/// <summary>
/// A fact that only runs when a live Jellyfin server is provided via
/// environment variables; otherwise the test is skipped. All tests using
/// this attribute are strictly read-only (GET/HEAD) against that server.
/// <code>
///   export SERVERSYNC_TEST_SERVER_URL=http://localhost:8096
///   export SERVERSYNC_TEST_API_KEY=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
///   dotnet test
/// </code>
/// </summary>
public sealed class ApiFactAttribute : FactAttribute
{
    public const string UrlVariable = "SERVERSYNC_TEST_SERVER_URL";
    public const string KeyVariable = "SERVERSYNC_TEST_API_KEY";

    public ApiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable)))
        {
            Skip = $"API integration tests skipped — set {UrlVariable} and {KeyVariable} to run them against a live server.";
        }
    }

    public static string ServerUrl => Environment.GetEnvironmentVariable(UrlVariable)!.TrimEnd('/');

    public static string ApiKey => Environment.GetEnvironmentVariable(KeyVariable)!;
}
