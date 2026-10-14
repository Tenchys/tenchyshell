using System.Threading;

namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Contadores agregados y opt-in para observar el coste de los hooks globales
/// del layout. No conserva coordenadas, teclas, HWND ni datos de sesión.
/// </summary>
public sealed class LayoutHookMetrics
{
    private long callbacks;
    private long mouseCallbacks;
    private long keyboardCallbacks;
    private long callbacksDuringDrag;
    private long overlayUpdatesPublished;
    private long cancellations;
    private long errors;
    private long totalDurationMicroseconds;
    private long maximumDurationMicroseconds;

    public void RecordCallback(LayoutHookType hookType, bool duringDrag, TimeSpan duration)
    {
        var durationMicroseconds = Math.Max(0, (long)Math.Round(duration.TotalMilliseconds * 1000d));
        Interlocked.Increment(ref callbacks);
        if (hookType == LayoutHookType.Mouse)
        {
            Interlocked.Increment(ref mouseCallbacks);
        }
        else
        {
            Interlocked.Increment(ref keyboardCallbacks);
        }

        if (duringDrag)
        {
            Interlocked.Increment(ref callbacksDuringDrag);
        }

        Interlocked.Add(ref totalDurationMicroseconds, durationMicroseconds);
        UpdateMaximum(ref maximumDurationMicroseconds, durationMicroseconds);
    }

    public void RecordOverlayUpdate() => Interlocked.Increment(ref overlayUpdatesPublished);

    public void RecordCancellation() => Interlocked.Increment(ref cancellations);

    public void RecordError() => Interlocked.Increment(ref errors);

    public LayoutHookMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref callbacks),
        Interlocked.Read(ref mouseCallbacks),
        Interlocked.Read(ref keyboardCallbacks),
        Interlocked.Read(ref callbacksDuringDrag),
        Interlocked.Read(ref overlayUpdatesPublished),
        Interlocked.Read(ref cancellations),
        Interlocked.Read(ref errors),
        Interlocked.Read(ref totalDurationMicroseconds) / 1000d,
        Interlocked.Read(ref maximumDurationMicroseconds) / 1000d);

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

public enum LayoutHookType
{
    Mouse,
    Keyboard
}

public readonly record struct LayoutHookMetricsSnapshot(
    long Callbacks,
    long MouseCallbacks,
    long KeyboardCallbacks,
    long CallbacksDuringDrag,
    long OverlayUpdatesPublished,
    long Cancellations,
    long Errors,
    double TotalDurationMilliseconds,
    double MaximumDurationMilliseconds);
