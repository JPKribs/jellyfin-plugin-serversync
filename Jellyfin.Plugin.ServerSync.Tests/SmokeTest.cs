using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests;

/// <summary>
/// The test project is wired up, so every other test can be trusted to run.
/// </summary>
public class SmokeTest
{
    /// <summary>
    /// Discovery smoke test for the test project itself.
    /// True: xUnit discovers and runs tests in this assembly.
    /// False: test project wiring is broken, investigate csproj before trusting any other test.
    /// </summary>
    [Fact]
    public void TestProjectIsWiredUp()
    {
        Assert.Equal(4, 2 + 2);
    }
}
