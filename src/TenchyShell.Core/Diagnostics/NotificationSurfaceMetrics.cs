namespace TenchyShell.Core.Diagnostics;

/// <summary>Contadores agregados del renderizado de avisos; no contiene datos de las notificaciones.</summary>
public sealed class NotificationSurfaceMetrics
{
    private const int PaintDurationSampleLimit = 128;
    private readonly long[] paintDurationSamples = new long[PaintDurationSampleLimit];
    private long repaints;
    private long iconDecodeAttempts;
    private long iconDecodeDurationMicroseconds;
    private long maximumIconDecodeDurationMicroseconds;
    private long paintDurationMicroseconds;
    private long maximumPaintDurationMicroseconds;
    private long iconBytesPresentedToDecoder;
    private long iconBytesWritten;
    private long paintDurationSampleCount;

    public void RecordPaint(TimeSpan duration)
    {
        var value = ToMicroseconds(duration);
        Interlocked.Increment(ref repaints);
        Interlocked.Add(ref paintDurationMicroseconds, value);
        UpdateMaximum(ref maximumPaintDurationMicroseconds, value);
        var sampleIndex = Interlocked.Increment(ref paintDurationSampleCount) - 1;
        paintDurationSamples[sampleIndex % PaintDurationSampleLimit] = value;
    }

    public void RecordIconDecode(TimeSpan duration, long inputBytesPresentedToDecoder)
    {
        var value = ToMicroseconds(duration);
        Interlocked.Increment(ref iconDecodeAttempts);
        Interlocked.Add(ref iconDecodeDurationMicroseconds, value);
        UpdateMaximum(ref maximumIconDecodeDurationMicroseconds, value);
        Interlocked.Add(ref iconBytesPresentedToDecoder, Math.Max(0, inputBytesPresentedToDecoder));
    }

    public void RecordIconBytesWritten(long bytes) => Interlocked.Add(ref iconBytesWritten, Math.Max(0, bytes));

    public NotificationSurfaceMetricsSnapshot Snapshot()
    {
        var sampleCount = (int)Math.Min(Volatile.Read(ref paintDurationSampleCount), PaintDurationSampleLimit);
        var p95 = sampleCount == 0
            ? 0
            : paintDurationSamples.AsSpan(0, sampleCount).ToArray().Order().ElementAt((int)Math.Ceiling(sampleCount * 0.95) - 1) / 1000d;
        return new NotificationSurfaceMetricsSnapshot(
            Interlocked.Read(ref repaints),
            Interlocked.Read(ref iconDecodeAttempts),
            Interlocked.Read(ref iconDecodeDurationMicroseconds) / 1000d,
            Interlocked.Read(ref maximumIconDecodeDurationMicroseconds) / 1000d,
            Interlocked.Read(ref paintDurationMicroseconds) / 1000d,
            Interlocked.Read(ref maximumPaintDurationMicroseconds) / 1000d,
            p95,
            Interlocked.Read(ref iconBytesPresentedToDecoder),
            Interlocked.Read(ref iconBytesWritten));
    }

    private static long ToMicroseconds(TimeSpan duration) => Math.Max(0, duration.Ticks / 10);

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}

public readonly record struct NotificationSurfaceMetricsSnapshot(
    long Repaints,
    long IconDecodeAttempts,
    double TotalIconDecodeDurationMilliseconds,
    double MaximumIconDecodeDurationMilliseconds,
    double TotalPaintDurationMilliseconds,
    double MaximumPaintDurationMilliseconds,
    double P95PaintDurationMilliseconds,
    long IconBytesPresentedToDecoder,
    long IconBytesWritten);
