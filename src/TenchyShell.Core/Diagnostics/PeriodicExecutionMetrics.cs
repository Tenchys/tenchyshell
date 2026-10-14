namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Agregado seguro de una ejecución periódica. No contiene identificadores de
/// elementos, comandos, argumentos ni salida de procesos.
/// </summary>
public sealed record PeriodicExecutionMetrics(
    string Executor,
    int ProcessesStarted,
    int OverlapsAvoided,
    int Cancellations,
    int Timeouts,
    int Errors,
    double? CpuMilliseconds,
    long? ReadBytes,
    long? WrittenBytes,
    long? PeakWorkingSetBytes,
    long LastValidOutputAgeMilliseconds,
    long DurationMilliseconds = 0)
{
    public static PeriodicExecutionMetrics Completed(
        string executor,
        bool succeeded,
        bool cancelled = false,
        bool timedOut = false,
        double? cpuMilliseconds = null,
        long? readBytes = null,
        long? writtenBytes = null,
        long? peakWorkingSetBytes = null,
        long lastValidOutputAgeMilliseconds = 0,
        long durationMilliseconds = 0) =>
        new(executor, 1, 0, cancelled ? 1 : 0, timedOut ? 1 : 0,
            succeeded || cancelled || timedOut ? 0 : 1,
            NonNegative(cpuMilliseconds), NonNegative(readBytes), NonNegative(writtenBytes),
            NonNegative(peakWorkingSetBytes), Math.Max(0, lastValidOutputAgeMilliseconds), Math.Max(0, durationMilliseconds));

    public static PeriodicExecutionMetrics OverlapAvoided(string executor) =>
        new(executor, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static double? NonNegative(double? value) => value is null ? null : Math.Max(0, value.Value);
    private static long? NonNegative(long? value) => value is null ? null : Math.Max(0, value.Value);
}
