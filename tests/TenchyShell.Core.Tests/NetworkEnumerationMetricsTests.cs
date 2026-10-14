using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Network;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class NetworkEnumerationMetricsTests
{
    [Fact]
    public void AggregatesSafeCountsAndDurations()
    {
        var metrics = new NetworkEnumerationMetrics();
        metrics.RecordInterfacesCounts(2, 1, 1, 1);
        metrics.RecordInterface(NetworkInterfaceKind.Unknown); // increments unknown
        metrics.RecordWifiNetworks(beforeDeduplication: 5, uniqueCount: 3);
        metrics.RecordEnumerationDurations(
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(7));
        metrics.RecordError();

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.EthernetInterfaces);
        Assert.Equal(1, snapshot.WifiInterfaces);
        Assert.Equal(1, snapshot.VpnInterfaces);
        Assert.Equal(2, snapshot.UnknownInterfaces);
        Assert.Equal(5, snapshot.WifiNetworksBeforeDeduplication);
        Assert.Equal(3, snapshot.UniqueWifiNetworks);
        Assert.Equal(3, snapshot.MaximumNetworksPerSnapshot);
        Assert.Equal(7, snapshot.TotalDurationMilliseconds, precision: 3);
        Assert.Equal(2, snapshot.InterfaceEnumerationDurationMilliseconds, precision: 3);
        Assert.Equal(4, snapshot.WifiEnumerationDurationMilliseconds, precision: 3);
        Assert.True(snapshot.HasError);
    }

    [Fact]
    public void DisabledMetricsRemainEmpty()
    {
        var metrics = new NetworkEnumerationMetrics(enabled: false);
        metrics.RecordInterfacesCounts(2, 1, 1, 1);
        metrics.RecordWifiNetworks(5, 3);
        metrics.RecordEnumerationDurations(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(7));
        metrics.RecordError();

        Assert.Equal(default, metrics.Snapshot());
    }

    [Fact]
    public void RecorderPublishesSanitizedNetworkAggregateWhenEnabled()
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
            recorder.RecordNetworkEnumerationMetrics(new NetworkEnumerationMetricsSnapshot(
                EthernetInterfaces: -1,
                WifiInterfaces: 2,
                VpnInterfaces: 1,
                UnknownInterfaces: 1,
                WifiNetworksBeforeDeduplication: 4,
                UniqueWifiNetworks: 3,
                MaximumNetworksPerSnapshot: 3,
                TotalDurationMilliseconds: -3,
                InterfaceEnumerationDurationMilliseconds: 2,
                WifiEnumerationDurationMilliseconds: 4,
                HasError: true));

            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(liveDirectory, "live-*.jsonl")));
            Assert.Contains("\"name\":\"network_enumeration\"", text, StringComparison.Ordinal);
            Assert.Contains("\"ethernetInterfaces\":0", text, StringComparison.Ordinal);
            Assert.Contains("\"totalDurationMilliseconds\":0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SSID", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RecorderDoesNotPublishNetworkAggregateWhenDisabled()
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
            recorder.RecordNetworkEnumerationMetrics(new NetworkEnumerationMetricsSnapshot(
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, true));

            Assert.False(Directory.Exists(liveDirectory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TenchyShell.NetworkEnumerationMetricsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}