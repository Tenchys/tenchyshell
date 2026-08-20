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
    public void CaptureMetadataRequiresSchemaTwoComparableEnvironmentAndFiveRuns()
    {
        var valid = new PerformanceCaptureMetadata(2, "TenchyShell", "Idle", "10.0.26100", 16, 32L * 1024 * 1024 * 1024, 5, 30);
        var invalid = new PerformanceCaptureMetadata(1, "Unknown", "Warm", "", 0, 0, 1, 1);

        Assert.Empty(PerformanceCaptureValidator.Validate(valid));
        var errors = PerformanceCaptureValidator.Validate(invalid);
        Assert.Contains(errors, error => error.Contains("schemaVersion"));
        Assert.Contains(errors, error => error.Contains("scenario"));
        Assert.Contains(errors, error => error.Contains("cinco"));
    }

    [Fact]
    public void CaptureAllowsOneExplicitSmokeRunAndRestrictsStressToTenchyShell()
    {
        var smoke = new PerformanceCaptureMetadata(2, "TenchyShell", "CommonWorkflow", "10.0.26200", 16, 32L * 1024 * 1024 * 1024, 1, 6, true);
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
    [InlineData(PerformanceScenario.TenchyShell, "TenchyShell.exe", PerformanceProcessRole.Shell)]
    [InlineData(PerformanceScenario.Explorer, "explorer.exe", PerformanceProcessRole.Shell)]
    [InlineData(PerformanceScenario.TenchyShell, "wezterm-gui.exe", PerformanceProcessRole.Tool)]
    [InlineData(PerformanceScenario.Explorer, "yazi.exe", PerformanceProcessRole.Tool)]
    public void ClassifierAssignsExplicitRoles(
        PerformanceScenario scenario,
        string processName,
        PerformanceProcessRole expected)
    {
        Assert.Equal(expected, PerformanceProcessClassifier.ClassifyRoot(scenario, processName));
    }
}
