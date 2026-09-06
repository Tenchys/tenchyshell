using TenchyShell.Core.Performance;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public void StatisticsCalculateInterpolatedQuartilesMedianAndPercentile()
    {
        var result = PerformanceStatistics.Summarize(new[] { 40d, 10d, 30d, 20d });

        Assert.Equal(4, result.Count);
        Assert.Equal(10, result.Minimum);
        Assert.Equal(40, result.Maximum);
        Assert.Equal(25, result.Median);
        Assert.Equal(38.5, result.Percentile95, precision: 6);
        Assert.Equal(17.5, result.Quartile1, precision: 6);
        Assert.Equal(32.5, result.Quartile3, precision: 6);
        Assert.Equal(15, result.InterquartileRange, precision: 6);
    }

    [Fact]
    public void StatisticsRejectEmptyOrNonFiniteSamples()
    {
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Summarize(Array.Empty<double>()));
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Summarize(new[] { double.NaN, double.PositiveInfinity }));
    }

    [Fact]
    public void CaptureMetadataRequiresSchemaThreeComparableEnvironmentAndFiveRuns()
    {
        var valid = new PerformanceCaptureMetadata(3, "TenchyShell", "Idle", "10.0.26100", 16, 32L * 1024 * 1024 * 1024, 5, 30);
        var invalid = new PerformanceCaptureMetadata(2, "Unknown", "Warm", "", 0, 0, 1, 1);

        Assert.Empty(PerformanceCaptureValidator.Validate(valid));
        var errors = PerformanceCaptureValidator.Validate(invalid);
        Assert.Contains(errors, error => error.Contains("schemaVersion"));
        Assert.Contains(errors, error => error.Contains("scenario"));
        Assert.Contains(errors, error => error.Contains("cinco"));
    }

    [Fact]
    public void CaptureAllowsOneExplicitSmokeRunAndRestrictsStressToTenchyShell()
    {
        var smoke = new PerformanceCaptureMetadata(3, "TenchyShell", "CommonWorkflow", "10.0.26200", 16, 32L * 1024 * 1024 * 1024, 1, 6, true);
        var invalidStress = smoke with { Scenario = "Explorer", Phase = "TenchyShellStress" };

        Assert.Empty(PerformanceCaptureValidator.Validate(smoke));
        Assert.Contains(PerformanceCaptureValidator.Validate(invalidStress), error => error.Contains("solo es válido"));
    }

    [Fact]
    public void DeltaHandlesZeroBaselineWithoutInventingAPercentage()
    {
        Assert.Equal(new PerformanceDelta(5, 50), PerformanceStatistics.Delta(10, 15));
        Assert.Equal(new PerformanceDelta(5, null), PerformanceStatistics.Delta(0, 5));
    }

    [Theory]
    [InlineData(PerformanceScenario.TenchyShell, "TenchyShell.exe", true)]
    [InlineData(PerformanceScenario.TenchyShell, "MinimalShell", true)]
    [InlineData(PerformanceScenario.TenchyShell, "explorer.exe", false)]
    [InlineData(PerformanceScenario.Explorer, "explorer.exe", true)]
    [InlineData(PerformanceScenario.Explorer, "TenchyShell.exe", false)]
    [InlineData(PerformanceScenario.Explorer, "wezterm-gui.exe", true)]
    [InlineData(PerformanceScenario.TenchyShell, "yazi.exe", true)]
    [InlineData(PerformanceScenario.TenchyShell, "msedge.exe", false)]
    public void ClassifierKeepsScenariosEquivalentAndExcludesBrowsers(
        PerformanceScenario scenario,
        string processName,
        bool expected)
    {
        Assert.Equal(expected, PerformanceProcessClassifier.IsIncludedRoot(scenario, processName));
    }

    [Theory]
    [InlineData(PerformanceScenario.TenchyShell, "TenchyShell.exe", PerformanceProcessRole.Core)]
    [InlineData(PerformanceScenario.Explorer, "explorer.exe", PerformanceProcessRole.Core)]
    [InlineData(PerformanceScenario.TenchyShell, "wezterm-gui.exe", PerformanceProcessRole.Tool)]
    [InlineData(PerformanceScenario.Explorer, "yazi.exe", PerformanceProcessRole.Tool)]
    public void ClassifierAssignsExplicitRoles(
        PerformanceScenario scenario,
        string processName,
        PerformanceProcessRole expected)
    {
        Assert.Equal(expected, PerformanceProcessClassifier.ClassifyRoot(scenario, processName));
    }

    [Fact]
    public void ClassifierUsesExactCoreAndSeparatesAuxiliaryToolAndObserverTrees()
    {
        var processes = new[]
        {
            new PerformanceProcessDescriptor(10, 1, "TenchyShell.exe"),
            new PerformanceProcessDescriptor(11, 10, "wezterm-gui.exe"),
            new PerformanceProcessDescriptor(12, 11, "TenchyShell.exe"),
            new PerformanceProcessDescriptor(13, 4, "TenchyShell.NotificationBridge.exe"),
            new PerformanceProcessDescriptor(20, 99, "wezterm-gui.exe"),
            new PerformanceProcessDescriptor(21, 20, "yazi.exe"),
            new PerformanceProcessDescriptor(99, 1, "pwsh.exe")
        };

        var roles = PerformanceProcessClassifier.ClassifyProcesses(
            PerformanceScenario.TenchyShell, 10, 99, processes);

        Assert.Equal(PerformanceProcessRole.Core, roles[10]);
        Assert.Equal(PerformanceProcessRole.Auxiliary, roles[11]);
        Assert.Equal(PerformanceProcessRole.Auxiliary, roles[12]);
        Assert.Equal(PerformanceProcessRole.Auxiliary, roles[13]);
        Assert.Equal(PerformanceProcessRole.Tool, roles[20]);
        Assert.Equal(PerformanceProcessRole.Tool, roles[21]);
        Assert.DoesNotContain(99, roles.Keys);
        Assert.Single(roles, pair => pair.Value == PerformanceProcessRole.Core);
    }

    [Fact]
    public void ClassifierRejectsMissingDuplicateObserverOrWrongCore()
    {
        var valid = new[] { new PerformanceProcessDescriptor(10, 1, "TenchyShell.exe") };

        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(PerformanceScenario.TenchyShell, 20, 99, valid));
        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(PerformanceScenario.Explorer, 10, 99, valid));
        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(PerformanceScenario.TenchyShell, 10, 10, valid));
        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(
            PerformanceScenario.TenchyShell,
            10,
            99,
            new[] { valid[0], valid[0] }));
    }

    [Fact]
    public void ClassifierRejectsExitedReusedOrOtherSessionCore()
    {
        var startedAt = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var otherSession = new[] { new PerformanceProcessDescriptor(10, 1, "TenchyShell.exe", 8, startedAt) };
        var exited = new[] { otherSession[0] with { SessionId = 7, HasExited = true } };
        var reused = new[] { otherSession[0] with { SessionId = 7, StartedAt = startedAt.AddSeconds(1) } };

        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(
            PerformanceScenario.TenchyShell, 10, 99, otherSession, measurementSessionId: 7, expectedMainStartTime: startedAt));
        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(
            PerformanceScenario.TenchyShell, 10, 99, exited, measurementSessionId: 7, expectedMainStartTime: startedAt));
        Assert.Throws<ArgumentException>(() => PerformanceProcessClassifier.ClassifyProcesses(
            PerformanceScenario.TenchyShell, 10, 99, reused, measurementSessionId: 7, expectedMainStartTime: startedAt));
    }
}
