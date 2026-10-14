using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class NotificationSurfaceMetricsTests
{
    [Fact]
    public void SurfaceMetricsAggregatePaintAndIconDecodeCosts()
    {
        var metrics = new NotificationSurfaceMetrics();

        metrics.RecordPaint(TimeSpan.FromMilliseconds(3));
        metrics.RecordPaint(TimeSpan.FromMilliseconds(7));
        metrics.RecordIconDecode(TimeSpan.FromMilliseconds(2), inputBytesPresentedToDecoder: 512);
        metrics.RecordIconBytesWritten(512);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.Repaints);
        Assert.Equal(1, snapshot.IconDecodeAttempts);
        Assert.Equal(2, snapshot.TotalIconDecodeDurationMilliseconds, precision: 3);
        Assert.Equal(2, snapshot.MaximumIconDecodeDurationMilliseconds, precision: 3);
        Assert.Equal(10, snapshot.TotalPaintDurationMilliseconds, precision: 3);
        Assert.Equal(7, snapshot.MaximumPaintDurationMilliseconds, precision: 3);
        Assert.Equal(7, snapshot.P95PaintDurationMilliseconds, precision: 3);
        Assert.Equal(512, snapshot.IconBytesPresentedToDecoder);
        Assert.Equal(512, snapshot.IconBytesWritten);
    }

    [Fact]
    public void PaintP95UsesBoundedRecentSamples()
    {
        var metrics = new NotificationSurfaceMetrics();

        for (var index = 0; index < 128; index++) metrics.RecordPaint(TimeSpan.FromMilliseconds(100));
        for (var index = 0; index < 128; index++) metrics.RecordPaint(TimeSpan.FromMilliseconds(1));

        var snapshot = metrics.Snapshot();

        Assert.Equal(256, snapshot.Repaints);
        Assert.Equal(1, snapshot.P95PaintDurationMilliseconds, precision: 3);
    }

    [Fact]
    public void RecorderPublishesOnlySanitizedNotificationAggregatesWhenEnabled()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var liveDirectory = Path.Combine(directory, "live");
            using var recorder = new LiveBenchmarkRecorder(
                new BenchmarkConfiguration
                {
                    Enabled = true,
                    SampleIntervalSeconds = 3600,
                    RetentionDays = 14,
                    MaxStorageMb = 16
                },
                new FileLogger(directory),
                liveDirectory);

            recorder.Start();
            recorder.RecordNotificationCenterMetrics(new NotificationCenterMetricsSnapshot(
                10, 5, 1200, 2, 300, 3, 8, 2400, 4, 400, 2, 1, 500, 2, 200, 1, 1, 2, 4, 2));
            recorder.RecordNotificationSurfaceMetrics(new NotificationSurfaceMetricsSnapshot(
                5, 2, 3, 2, 12, 5, 4, 1024, 512));

            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(liveDirectory, "live-*.jsonl")));
            Assert.Contains("\"name\":\"notification_center_metrics\"", text, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"notification_surface_metrics\"", text, StringComparison.Ordinal);
            Assert.Contains("\"addAllocatedBytes\":1200", text, StringComparison.Ordinal);
            Assert.Contains("\"p95PaintDurationMilliseconds\":4", text, StringComparison.Ordinal);
            Assert.DoesNotContain("title", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("body", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notificationId", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RecorderDoesNotPublishNotificationMetricsWhenDisabled()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var liveDirectory = Path.Combine(directory, "live");
            using var recorder = new LiveBenchmarkRecorder(
                new BenchmarkConfiguration { Enabled = false },
                new FileLogger(directory),
                liveDirectory);

            recorder.Start();
            recorder.RecordNotificationCenterMetrics(default);
            recorder.RecordNotificationSurfaceMetrics(default);

            Assert.False(Directory.Exists(liveDirectory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TenchyShell.NotificationMetricsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
