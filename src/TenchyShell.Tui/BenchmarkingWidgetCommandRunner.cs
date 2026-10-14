using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;

namespace TenchyShell.Tui;

/// <summary>Observa solo el resultado agregado de widgets de comando.</summary>
public sealed class BenchmarkingWidgetCommandRunner : IWidgetCommandRunner
{
    private readonly IWidgetCommandRunner inner;
    private readonly LiveBenchmarkRecorder? recorder;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, DateTimeOffset> lastValidOutputAt = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();

    public BenchmarkingWidgetCommandRunner(IWidgetCommandRunner inner, LiveBenchmarkRecorder? recorder, Func<DateTimeOffset>? clock = null)
    {
        this.inner = inner;
        this.recorder = recorder;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<WidgetExecutionResult> ExecuteAsync(TuiWidgetConfiguration widget, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await inner.ExecuteAsync(widget, cancellationToken).ConfigureAwait(false);
            var resources = result.Resources;
            var now = clock();
            long lastValidOutputAgeMilliseconds;
            lock (syncRoot)
            {
                if (result.Succeeded) lastValidOutputAt[widget.Id] = now;
                lastValidOutputAgeMilliseconds = lastValidOutputAt.TryGetValue(widget.Id, out var lastValidAt)
                    ? Math.Max(0, (long)(now - lastValidAt).TotalMilliseconds)
                    : 0;
            }
            recorder?.RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics.Completed(
                "tui", result.Succeeded, cancellationToken.IsCancellationRequested, result.TimedOut,
                resources?.CpuMilliseconds, resources?.ReadBytes, resources?.WrittenBytes, resources?.PeakWorkingSetBytes,
                lastValidOutputAgeMilliseconds));
            return result;
        }
        catch
        {
            recorder?.RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics.Completed(
                "tui", succeeded: false, cancelled: cancellationToken.IsCancellationRequested));
            throw;
        }
    }
}
