namespace TenchyShell.Core.Performance;

public sealed record PerformanceSummary(
    int Count,
    double Minimum,
    double Maximum,
    double Median,
    double Percentile95,
    double Quartile1,
    double Quartile3)
{
    public double InterquartileRange => Quartile3 - Quartile1;
}

public sealed record PerformanceDelta(double Absolute, double? Percentage);

public static class PerformanceStatistics
{
    public static PerformanceSummary Summarize(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples
            .Where(double.IsFinite)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("Se requiere al menos una muestra finita.", nameof(samples));
        }

        return new PerformanceSummary(
            ordered.Length,
            ordered[0],
            ordered[^1],
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.25),
            Percentile(ordered, 0.75));
    }

    public static PerformanceDelta Delta(double baseline, double candidate)
    {
        if (!double.IsFinite(baseline)) throw new ArgumentOutOfRangeException(nameof(baseline));
        if (!double.IsFinite(candidate)) throw new ArgumentOutOfRangeException(nameof(candidate));

        var absolute = candidate - baseline;
        return new PerformanceDelta(
            absolute,
            baseline == 0 ? null : absolute / baseline * 100);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 1) return ordered[0];
        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        var weight = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
    }
}

public sealed record PerformanceCaptureMetadata(
    int SchemaVersion,
    string Scenario,
    string Phase,
    string WindowsVersion,
    int LogicalProcessors,
    long MemoryBytes,
    int Repetitions,
    int SamplesPerRepetition,
    bool IsSmokeTest = false);

public static class PerformanceCaptureValidator
{
    public static IReadOnlyList<string> Validate(PerformanceCaptureMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var errors = new List<string>();
        if (metadata.SchemaVersion != 2) errors.Add("schemaVersion debe ser 2.");
        if (!Enum.TryParse<PerformanceScenario>(metadata.Scenario, ignoreCase: true, out _)) errors.Add("scenario no es válido.");
        if (!Enum.TryParse<PerformancePhase>(metadata.Phase, ignoreCase: true, out var phase))
        {
            errors.Add("phase debe ser Idle, CommonWorkflow o TenchyShellStress.");
        }
        else if (phase == PerformancePhase.TenchyShellStress &&
                 !metadata.Scenario.Equals(nameof(PerformanceScenario.TenchyShell), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("TenchyShellStress solo es válido para el escenario TenchyShell.");
        }
        if (string.IsNullOrWhiteSpace(metadata.WindowsVersion)) errors.Add("windowsVersion es obligatorio.");
        if (metadata.LogicalProcessors <= 0) errors.Add("logicalProcessors debe ser mayor que cero.");
        if (metadata.MemoryBytes <= 0) errors.Add("memoryBytes debe ser mayor que cero.");
        if (metadata.IsSmokeTest)
        {
            if (metadata.Repetitions != 1) errors.Add("Un smoke test debe contener exactamente una repetición.");
        }
        else if (metadata.Repetitions < 5)
        {
            errors.Add("Se requieren al menos cinco repeticiones.");
        }
        if (metadata.SamplesPerRepetition < 2) errors.Add("Se requieren al menos dos muestras por repetición.");
        return errors;
    }
}
