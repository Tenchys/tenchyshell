using TenchyShell.Core.StatusPanel;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class StatusPanelTimerPolicyTests
{
    [Fact]
    public void VisiblePanelUses250Milliseconds()
    {
        Assert.Equal(250u, StatusPanelTimerPolicy.GetIntervalMilliseconds(visible: true));
    }

    [Fact]
    public void HiddenPanelUsesAtMost500MillisecondsForEdgeReveal()
    {
        var interval = StatusPanelTimerPolicy.GetIntervalMilliseconds(visible: false);

        Assert.Equal(500u, interval);
        Assert.True(interval <= 500u);
    }

    [Fact]
    public void PolicyHasOnlyTheTwoDocumentedCadences()
    {
        Assert.Equal(StatusPanelTimerPolicy.VisibleIntervalMilliseconds, 250u);
        Assert.Equal(StatusPanelTimerPolicy.HiddenIntervalMilliseconds, 500u);
    }
}
