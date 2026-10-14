namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Contadores agregados y opt-in del renderizado de la bandeja propia. No
/// contiene rutas, títulos, HWND ni datos de sesión.
/// </summary>
public sealed class SystemTrayMetrics
{
    private long repaints;
    private long iconLoadsSucceeded;
    private long iconLoadsFailed;
    private long totalPaintDurationMicroseconds;
    private long maximumPaintDurationMicroseconds;
    private long readBytes;
    private long writtenBytes;
    private int readBytesObserved;
    private int writtenBytesObserved;
    private long gdiHandlesBefore = -1;
    private long gdiHandlesAfter = -1;

    public void RecordIconLoad(SystemTrayIconLoadStatus status)
    {
        if (status == SystemTrayIconLoadStatus.Loaded)
        {
            Interlocked.Increment(ref iconLoadsSucceeded);
        }
        else if (status != SystemTrayIconLoadStatus.NotConfigured)
        {
            Interlocked.Increment(ref iconLoadsFailed);
        }
    }

    public void RecordPaint(
        TimeSpan duration,
        long? gdiHandlesBefore,
        long? gdiHandlesAfter,
        long? readBytesForPaint,
        long? writtenBytesForPaint)
    {
        var durationMicroseconds = Math.Max(0, duration.Ticks / 10);
        Interlocked.Increment(ref repaints);
        Interlocked.Add(ref totalPaintDurationMicroseconds, durationMicroseconds);
        UpdateMaximum(ref maximumPaintDurationMicroseconds, durationMicroseconds);
        RecordOptionalCounter(ref this.gdiHandlesBefore, gdiHandlesBefore);
        RecordOptionalCounter(ref this.gdiHandlesAfter, gdiHandlesAfter);
        RecordOptionalBytes(ref readBytes, ref readBytesObserved, readBytesForPaint);
        RecordOptionalBytes(ref writtenBytes, ref writtenBytesObserved, writtenBytesForPaint);
    }

    public SystemTrayMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref repaints),
        Interlocked.Read(ref iconLoadsSucceeded),
        Interlocked.Read(ref iconLoadsFailed),
        Interlocked.Read(ref totalPaintDurationMicroseconds) / 1000d,
        Interlocked.Read(ref maximumPaintDurationMicroseconds) / 1000d,
        ReadOptionalCounter(readBytes, readBytesObserved),
        ReadOptionalCounter(writtenBytes, writtenBytesObserved),
        ReadOptionalCounter(gdiHandlesBefore, gdiHandlesBefore == -1 ? 0 : 1),
        ReadOptionalCounter(gdiHandlesAfter, gdiHandlesAfter == -1 ? 0 : 1));

    private static void RecordOptionalBytes(ref long target, ref int observed, long? value)
    {
        if (value is null) return;
        Interlocked.Exchange(ref observed, 1);
        Interlocked.Add(ref target, Math.Max(0, value.Value));
    }

    private static void RecordOptionalCounter(ref long target, long? value)
    {
        if (value is null) return;
        Interlocked.Exchange(ref target, Math.Max(0, value.Value));
    }

    private static long? ReadOptionalCounter(long value, long observed) => observed == 0 ? null : Math.Max(0, value);

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

public enum SystemTrayIconLoadStatus
{
    NotConfigured,
    InvalidPath,
    Failed,
    Loaded
}

public readonly record struct SystemTrayMetricsSnapshot(
    long Repaints,
    long IconLoadsSucceeded,
    long IconLoadsFailed,
    double TotalPaintDurationMilliseconds,
    double MaximumPaintDurationMilliseconds,
    long? ReadBytes,
    long? WrittenBytes,
    long? GdiHandlesBefore,
    long? GdiHandlesAfter);
