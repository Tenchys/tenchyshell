using TenchyShell.Core.Diagnostics;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class LayoutHookMetricsTests
{
    [Fact]
    public void SnapshotContainsOnlyAggregatedHookCountersAndDurations()
    {
        var metrics = new LayoutHookMetrics();

        metrics.RecordCallback(LayoutHookType.Mouse, duringDrag: false, TimeSpan.FromMilliseconds(1.2));
        metrics.RecordCallback(LayoutHookType.Keyboard, duringDrag: true, TimeSpan.FromMilliseconds(2.4));
        metrics.RecordOverlayUpdate();
        metrics.RecordCancellation();
        metrics.RecordError();

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.Callbacks);
        Assert.Equal(1, snapshot.MouseCallbacks);
        Assert.Equal(1, snapshot.KeyboardCallbacks);
        Assert.Equal(1, snapshot.CallbacksDuringDrag);
        Assert.Equal(1, snapshot.OverlayUpdatesPublished);
        Assert.Equal(1, snapshot.Cancellations);
        Assert.Equal(1, snapshot.Errors);
        Assert.Equal(3.6, snapshot.TotalDurationMilliseconds, precision: 6);
        Assert.Equal(2.4, snapshot.MaximumDurationMilliseconds, precision: 6);
    }
}
