using TenchyShell.Core.Runtime;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class CoalescedRefreshSchedulerTests
{
    [Fact]
    public void RequestRejectsWhenTheMessageLoopDoesNotAcceptTheRefresh()
    {
        using var scheduler = new CoalescedRefreshScheduler(_ => false, () => throw new Xunit.Sdk.XunitException("No debe ejecutarse."), _ => { });

        Assert.False(scheduler.Request());
    }

    [Fact]
    public void BurstSchedulesOnlyOnePendingRefresh()
    {
        var scheduled = new Queue<Action>();
        var refreshCount = 0;
        using var scheduler = new CoalescedRefreshScheduler(action => { scheduled.Enqueue(action); return true; }, () => refreshCount++, _ => { });

        Assert.True(scheduler.Request());
        Assert.True(scheduler.Request());
        Assert.True(scheduler.Request());

        Assert.Single(scheduled);
        scheduled.Dequeue()();
        Assert.Equal(1, refreshCount);
    }

    [Fact]
    public void EventDuringRefreshSchedulesExactlyOneAdditionalPass()
    {
        var scheduled = new Queue<Action>();
        CoalescedRefreshScheduler? scheduler = null;
        var refreshCount = 0;
        scheduler = new CoalescedRefreshScheduler(action => { scheduled.Enqueue(action); return true; }, () =>
        {
            refreshCount++;
            scheduler!.Request();
            scheduler.Request();
        }, _ => { });
        using (scheduler)
        {
            scheduler.Request();
            scheduled.Dequeue()();

            Assert.Single(scheduled);
            scheduled.Dequeue()();
            Assert.Equal(2, refreshCount);
        }
    }

    [Fact]
    public void DisposeClearsAPendingRefresh()
    {
        var scheduled = new Queue<Action>();
        var refreshCount = 0;
        var scheduler = new CoalescedRefreshScheduler(action => { scheduled.Enqueue(action); return true; }, () => refreshCount++, _ => { });

        scheduler.Request();
        scheduler.Dispose();
        scheduled.Dequeue()();

        Assert.Equal(0, refreshCount);
        Assert.False(scheduler.Request());
    }

    [Fact]
    public void RefreshFailureIsReportedWithoutEscapingTheScheduledAction()
    {
        var scheduled = new Queue<Action>();
        Exception? captured = null;
        using var scheduler = new CoalescedRefreshScheduler(action => { scheduled.Enqueue(action); return true; },
            () => throw new InvalidOperationException("optional refresh failed"), exception => captured = exception);

        scheduler.Request();
        scheduled.Dequeue()();

        Assert.IsType<InvalidOperationException>(captured);
    }
}
