using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Notifications;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class NotificationCenterMetricsTests
{
    [Fact]
    public void NotificationCenterRecordsOnlyAggregateOperationMetricsWhenEnabled()
    {
        var metrics = new NotificationCenterMetrics(enabled: true);
        var center = new NotificationCenter(metrics);

        center.Add(new ShellNotification("private-id", "private-app", "Private app", "Private title", "Private body", DateTimeOffset.UtcNow));
        _ = center.GetActive();
        center.Remove("private-id");
        for (var index = 0; index <= NotificationCenter.MaximumHistoryItems; index++)
        {
            center.Add(new ShellNotification($"id-{index}", "app", "app", "title", "body", DateTimeOffset.UtcNow.AddTicks(index)));
        }

        var snapshot = metrics.Snapshot();
        Assert.Equal(1 + NotificationCenter.MaximumHistoryItems + 1, snapshot.AddCount);
        Assert.Equal(1, snapshot.GetActiveCount);
        Assert.Equal(1, snapshot.RemoveCount);
        Assert.Equal(1, snapshot.TrimCalls);
        Assert.Equal(1, snapshot.TrimmedItems);
        Assert.True(snapshot.AddAllocatedBytes > 0);
        Assert.True(snapshot.GetActiveAllocatedBytes > 0);
        Assert.True(snapshot.AddDurationP95Milliseconds >= 0);
        Assert.True(snapshot.GetActiveDurationP95Milliseconds >= 0);
        Assert.True(snapshot.RemoveDurationP95Milliseconds >= 0);
    }

    [Fact]
    public void DisabledNotificationMetricsRemainEmptyAndDoNotInstrumentCenter()
    {
        var metrics = new NotificationCenterMetrics(enabled: false);
        var center = new NotificationCenter(metrics);

        center.Add(new ShellNotification("id", "app", "app", "title", "body", DateTimeOffset.UtcNow));
        _ = center.GetActive();
        center.Remove("id");

        Assert.Equal(default, metrics.Snapshot());
    }
}
