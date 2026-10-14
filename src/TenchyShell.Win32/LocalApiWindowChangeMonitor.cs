namespace TenchyShell.Win32;

using TenchyShell.Core.Logging;
using TenchyShell.Core.Runtime;
using TenchyShell.Core.Diagnostics;

public sealed class LocalApiWindowChangeMonitor : IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly CoalescedRefreshScheduler refreshScheduler;
    private readonly NativeMethods.WinEventProc callback;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private IntPtr hook;
    private bool disposed;

    public LocalApiWindowChangeMonitor(MessageLoopHost messageLoop, Action refresh, ILogger logger, LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.messageLoop = messageLoop;
        refreshScheduler = new CoalescedRefreshScheduler(messageLoop.TryPost, refresh,
            exception => logger.Error("Falló el refresco opcional de ventanas para la API local.", exception));
        callback = OnWinEvent;
        this.benchmarkRecorder = benchmarkRecorder;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hook != IntPtr.Zero) return;
        hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero,
            callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (hook == IntPtr.Zero) throw new InvalidOperationException("No se pudo registrar el hook de cambios de ventanas para la API local.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }
        refreshScheduler.Dispose();
    }

    private void OnWinEvent(IntPtr hookHandle, uint eventType, IntPtr windowHandle, int objectId, int childId, uint threadId, uint eventTime)
    {
        if (windowHandle == IntPtr.Zero || objectId != NativeMethods.OBJID_WINDOW || childId != 0) return;
        refreshScheduler.Request();
        benchmarkRecorder?.RecordAtMostEverySampleInterval("local_api_win32_events", new
        {
            received = refreshScheduler.Requests,
            coalesced = refreshScheduler.CoalescedRequests
        });
    }
}
