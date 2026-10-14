using System.Diagnostics;

namespace TenchyShell.Core.Diagnostics;

/// <summary>Agregados opt-in de operaciones del historial de notificaciones.</summary>
public sealed class NotificationCenterMetrics
{
    private long addCount, addTicks, addAllocated, addMaxTicks, addMaxAllocated;
    private long getCount, getTicks, getAllocated, getMaxTicks, getMaxAllocated;
    private long removeCount, removeTicks, removeAllocated, removeMaxTicks, removeMaxAllocated;
    private long trimCount, trimItems;
    private readonly long[]? addDurationSamples;
    private readonly long[]? getDurationSamples;
    private readonly long[]? removeDurationSamples;
    private long addSampleIndex, getSampleIndex, removeSampleIndex;

    public NotificationCenterMetrics(bool enabled)
    {
        IsEnabled = enabled;
        if (enabled)
        {
            addDurationSamples = new long[256];
            getDurationSamples = new long[256];
            removeDurationSamples = new long[256];
        }
    }

    public bool IsEnabled { get; }

    internal long StartTimestamp() => Stopwatch.GetTimestamp();

    internal long StartAllocationCount() => GC.GetAllocatedBytesForCurrentThread();

    internal void RecordAdd(long started, long allocated, int trimmed)
    {
        var elapsed = Record(ref addCount, ref addTicks, ref addAllocated, ref addMaxTicks, ref addMaxAllocated, started, allocated);
        Sample(addDurationSamples!, ref addSampleIndex, elapsed);
        if (trimmed > 0)
        {
            Interlocked.Increment(ref trimCount);
            Interlocked.Add(ref trimItems, trimmed);
        }
    }

    internal void RecordGetActive(long started, long allocated) =>
        Sample(getDurationSamples!, ref getSampleIndex,
            Record(ref getCount, ref getTicks, ref getAllocated, ref getMaxTicks, ref getMaxAllocated, started, allocated));

    internal void RecordRemove(long started, long allocated) =>
        Sample(removeDurationSamples!, ref removeSampleIndex,
            Record(ref removeCount, ref removeTicks, ref removeAllocated, ref removeMaxTicks, ref removeMaxAllocated, started, allocated));

    public NotificationCenterMetricsSnapshot Snapshot() => new(
        Read(addCount), Milliseconds(addTicks), Read(addAllocated), Milliseconds(addMaxTicks), Read(addMaxAllocated),
        Read(getCount), Milliseconds(getTicks), Read(getAllocated), Milliseconds(getMaxTicks), Read(getMaxAllocated),
        Read(removeCount), Milliseconds(removeTicks), Read(removeAllocated), Milliseconds(removeMaxTicks), Read(removeMaxAllocated),
        Read(trimCount), Read(trimItems),
        P95(addDurationSamples, addSampleIndex), P95(getDurationSamples, getSampleIndex), P95(removeDurationSamples, removeSampleIndex));

    private static long Record(ref long count, ref long ticks, ref long allocated, ref long maxTicks, ref long maxAllocated, long started, long allocationStart)
    {
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - started);
        var bytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationStart);
        Interlocked.Increment(ref count);
        Interlocked.Add(ref ticks, elapsed);
        Interlocked.Add(ref allocated, bytes);
        UpdateMaximum(ref maxTicks, elapsed);
        UpdateMaximum(ref maxAllocated, bytes);
        return elapsed;
    }

    private static void Sample(long[] samples, ref long index, long elapsed)
    {
        var current = Interlocked.Increment(ref index) - 1;
        samples[current % samples.Length] = elapsed;
    }

    private static double P95(long[]? samples, long index)
    {
        if (samples is null || index == 0) return 0;
        var count = (int)Math.Min(index, samples.Length);
        var copy = new long[count];
        Array.Copy(samples, copy, count);
        Array.Sort(copy);
        var percentileIndex = Math.Max(0, (int)Math.Ceiling(count * 0.95d) - 1);
        return Milliseconds(copy[percentileIndex]);
    }

    private static long Read(long value) => Interlocked.Read(ref value);
    private static double Milliseconds(long stopwatchTicks) => stopwatchTicks * 1000d / Stopwatch.Frequency;

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}

public readonly record struct NotificationCenterMetricsSnapshot(
    long AddCount, double AddTotalMilliseconds, long AddAllocatedBytes, double AddMaximumMilliseconds, long AddMaximumAllocatedBytes,
    long GetActiveCount, double GetActiveTotalMilliseconds, long GetActiveAllocatedBytes, double GetActiveMaximumMilliseconds, long GetActiveMaximumAllocatedBytes,
    long RemoveCount, double RemoveTotalMilliseconds, long RemoveAllocatedBytes, double RemoveMaximumMilliseconds, long RemoveMaximumAllocatedBytes,
    long TrimCalls, long TrimmedItems,
    double AddDurationP95Milliseconds, double GetActiveDurationP95Milliseconds, double RemoveDurationP95Milliseconds);
