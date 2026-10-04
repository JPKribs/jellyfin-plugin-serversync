using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class HintDeliveryTests
{
    /// <summary>
    /// Every answer a peer can give maps to exactly one of four actions.
    /// True: a bad key pauses, a malformed hint fails for good, an outage retries, and 200 means held.
    /// False: a bad key would be retried forever or an outage would park rows permanently.
    /// </summary>
    [Theory]
    [InlineData(200, DeliveryOutcome.Accepted)]
    [InlineData(400, DeliveryOutcome.Malformed)]
    [InlineData(401, DeliveryOutcome.PausePeer)]
    [InlineData(403, DeliveryOutcome.PausePeer)]
    [InlineData(404, DeliveryOutcome.PausePeer)]
    [InlineData(409, DeliveryOutcome.PausePeer)]
    [InlineData(500, DeliveryOutcome.Retry)]
    [InlineData(502, DeliveryOutcome.Retry)]
    [InlineData(503, DeliveryOutcome.Retry)]
    [InlineData(0, DeliveryOutcome.Retry)]
    [InlineData(429, DeliveryOutcome.Retry)]
    [InlineData(428, DeliveryOutcome.PausePeer)]
    public void Classify_MapsStatusToOutcome(int status, DeliveryOutcome expected)
    {
        Assert.Equal(expected, HintDelivery.Classify(status));
    }

    /// <summary>
    /// The pause reason names the fix for the operator.
    /// True: the dashboard says whether to check the key, install the plugin, or list this server there.
    /// False: three different problems would read the same.
    /// </summary>
    [Fact]
    public void PauseReason_NamesTheFix()
    {
        Assert.Contains("administrator", HintDelivery.PauseReason(403, ""), System.StringComparison.Ordinal);
        Assert.Contains("not installed", HintDelivery.PauseReason(404, ""), System.StringComparison.Ordinal);
        Assert.Contains("does not list this server", HintDelivery.PauseReason(409, "server x is not configured"), System.StringComparison.Ordinal);
        Assert.Contains("could not pair", HintDelivery.PauseReason(428, "server x is not paired"), System.StringComparison.Ordinal);
        Assert.Contains("server x is not configured", HintDelivery.PauseReason(409, "server x is not configured"), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A sent hint the peer finished is completed, one it still holds waits, and one it neither holds nor
    /// finished is sent again.
    /// True: a receiver with a standard user's key, which cannot report completion, is still cleared up.
    /// False: its rows would stay sent forever, or a finished hint would be sent again every grace period.
    /// </summary>
    [Fact]
    public void Reconcile_SortsSentRows()
    {
        var held = new System.Collections.Generic.HashSet<string> { "a:2" };
        var completed = new System.Collections.Generic.HashSet<string> { "a:1" };
        var (complete, resend) = HintDelivery.Reconcile(new[] { "a:1", "a:2", "a:3" }, held, completed);
        Assert.Equal(new[] { "a:1" }, complete);
        Assert.Equal(new[] { "a:3" }, resend);
    }
}
