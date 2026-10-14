using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class SystemTrayMetricsTests
{
    [Fact]
    public void AggregatesIconPaintAndResourceCountersWithSafeValues()
    {
        var metrics = new SystemTrayMetrics();

        metrics.RecordIconLoad(SystemTrayIconLoadStatus.NotConfigured);
        metrics.RecordIconLoad(SystemTrayIconLoadStatus.Loaded);
        metrics.RecordIconLoad(SystemTrayIconLoadStatus.InvalidPath);
        metrics.RecordIconLoad(SystemTrayIconLoadStatus.Failed);
        metrics.RecordPaint(
            TimeSpan.FromMilliseconds(1.5),
            gdiHandlesBefore: 12,
            gdiHandlesAfter: 14,
            readBytesForPaint: -1,
            writtenBytesForPaint: 8);
        metrics.RecordPaint(
            TimeSpan.FromMilliseconds(-1),
            gdiHandlesBefore: -4,
            gdiHandlesAfter: -3,
            readBytesForPaint: null,
            writtenBytesForPaint: null);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.Repaints);
        Assert.Equal(1, snapshot.IconLoadsSucceeded);
        Assert.Equal(2, snapshot.IconLoadsFailed);
        Assert.Equal(1.5, snapshot.TotalPaintDurationMilliseconds, precision: 3);
        Assert.Equal(1.5, snapshot.MaximumPaintDurationMilliseconds, precision: 3);
        Assert.Equal(0, snapshot.ReadBytes);
        Assert.Equal(8, snapshot.WrittenBytes);
        Assert.Equal(0, snapshot.GdiHandlesBefore);
        Assert.Equal(0, snapshot.GdiHandlesAfter);
    }

    [Fact]
    public void RecorderPublishesSanitizedSystemTrayAggregateWhenEnabled()
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
            recorder.RecordSystemTrayMetrics(new SystemTrayMetricsSnapshot(
                Repaints: -1,
                IconLoadsSucceeded: 2,
                IconLoadsFailed: 1,
                TotalPaintDurationMilliseconds: -3,
                MaximumPaintDurationMilliseconds: 4,
                ReadBytes: -5,
                WrittenBytes: 6,
                GdiHandlesBefore: -7,
                GdiHandlesAfter: 8));

            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(liveDirectory, "live-*.jsonl")));
            Assert.Contains("\"name\":\"system_tray_metrics\"", text, StringComparison.Ordinal);
            Assert.Contains("\"repaints\":0", text, StringComparison.Ordinal);
            Assert.Contains("\"totalPaintDurationMilliseconds\":0", text, StringComparison.Ordinal);
            Assert.Contains("\"readBytes\":0", text, StringComparison.Ordinal);
            Assert.Contains("\"gdiHandlesBefore\":0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("HWND", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("title", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RecorderDoesNotPublishSystemTrayAggregateWhenDisabled()
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
            recorder.RecordSystemTrayMetrics(new SystemTrayMetricsSnapshot(
                1, 1, 1, 1, 1, 1, 1, 1, 1));

            Assert.False(Directory.Exists(liveDirectory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TenchyShell.SystemTrayMetricsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
