using TenchyShell.Core.Windows;
using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class DesktopAreaPolicyTests
{
    private static readonly WindowRect WorkArea = new(0, 0, 1920, 1040);
    private static readonly WindowRect MonitorArea = new(0, 0, 1920, 1080);

    [Fact]
    public void UsesWorkAreaWhileExplorerIsAvailable()
    {
        var policy = new DesktopAreaPolicy();
        var monitor = new WindowMonitor("primary", true, WorkArea, MonitorArea);

        Assert.Equal(WorkArea, policy.GetArea(monitor));
    }

    [Fact]
    public void UsesCompleteMonitorOnlyWhenEnabled()
    {
        var policy = new DesktopAreaPolicy { UseMonitorArea = true };
        var monitor = new WindowMonitor("primary", true, WorkArea, MonitorArea);

        Assert.Equal(MonitorArea, policy.GetArea(monitor));
    }
}
