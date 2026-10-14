using System.ComponentModel;
using System.Runtime.InteropServices;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;
using CoreWorkspaceWindowSnapshot = TenchyShell.Core.Windows.WorkspaceWindowSnapshot;

namespace TenchyShell.Win32;

/// <summary>
/// Oculta los HWND administrados cuando Windows termina de minimizarlos y los
/// vuelve a mostrar al restaurarse. Usa eventos Win32, no polling.
/// </summary>
public sealed class MinimizedWindowVisibilityService : IMinimizedWindowVisibilityService
{
    private readonly MessageLoopHost messageLoop;
    private readonly IWorkspaceWindowSource workspaceSource;
    private readonly IWorkspaceWindowService windowService;
    private readonly ILogger logger;
    private readonly NativeMethods.WinEventProc eventProcedure;
    private readonly HashSet<IntPtr> managedHiddenWindows = new();
    private IntPtr eventHook;
    private bool isDisposed;

    public MinimizedWindowVisibilityService(
        MessageLoopHost messageLoop,
        IWorkspaceWindowSource workspaceSource,
        IWorkspaceWindowService windowService,
        ILogger logger)
    {
        this.messageLoop = messageLoop;
        this.workspaceSource = workspaceSource;
        this.windowService = windowService;
        this.logger = logger;
        eventProcedure = OnWinEvent;
    }

    public bool IsManagedHidden(IntPtr windowHandle) => managedHiddenWindows.Contains(windowHandle);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (eventHook != IntPtr.Zero) return;

        eventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            eventProcedure,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (eventHook == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "No se pudo registrar el seguimiento de minimización de ventanas.");
        }

        Synchronize();
    }

    public void Synchronize()
    {
        if (isDisposed) return;

        var operation = workspaceSource.CaptureCurrentWorkspaceSnapshot("minimized_sync");
        foreach (var windowHandle in managedHiddenWindows.ToArray())
        {
            if (!windowService.WindowExists(windowHandle))
            {
                managedHiddenWindows.Remove(windowHandle);
            }
        }

        foreach (var snapshot in operation.CurrentWindows)
        {
            SynchronizeWindow(snapshot, operation);
        }
    }

    public void Forget(IntPtr windowHandle) => managedHiddenWindows.Remove(windowHandle);

    public void Dispose()
    {
        if (isDisposed) return;

        if (eventHook != IntPtr.Zero)
        {
            if (!NativeMethods.UnhookWinEvent(eventHook))
            {
                logger.Error($"No se pudo liberar el seguimiento de minimización. Código Win32: {Marshal.GetLastWin32Error()}.");
            }

            eventHook = IntPtr.Zero;
        }

        foreach (var windowHandle in managedHiddenWindows.ToArray())
        {
            if (NativeMethods.IsWindow(windowHandle))
            {
                windowService.SetVisible(windowHandle, visible: true);
            }
        }

        managedHiddenWindows.Clear();
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (isDisposed || objectId != NativeMethods.OBJID_WINDOW || childId != 0 || windowHandle == IntPtr.Zero)
        {
            return;
        }

        // WinEvent callbacks deben ser cortos; la consulta y el ShowWindow se
        // ejecutan en el hilo del message loop para serializarlos con workspaces.
        messageLoop.Post(() => SynchronizeWindow(windowHandle));
    }

    private void SynchronizeWindow(IntPtr windowHandle)
    {
        if (isDisposed) return;

        var operation = workspaceSource.CaptureCurrentWorkspaceSnapshot("minimized_event");
        var snapshot = operation.CurrentWindows.FirstOrDefault(item => item.Handle == windowHandle);
        if (snapshot.Handle == IntPtr.Zero) return;
        SynchronizeWindow(snapshot, operation);
    }

    private void SynchronizeWindow(CoreWorkspaceWindowSnapshot snapshot, WorkspaceOperationSnapshot operation)
    {
        if (isDisposed) return;

        if (!snapshot.Exists)
        {
            managedHiddenWindows.Remove(snapshot.Handle);
            return;
        }

        if (snapshot.IsMinimized)
        {
            if (!managedHiddenWindows.Contains(snapshot.Handle) && snapshot.IsVisible && snapshot.IsOperable)
            {
                if (windowService.RevalidateWindow(snapshot))
                {
                    windowService.SetVisible(snapshot.Handle, visible: false);
                    managedHiddenWindows.Add(snapshot.Handle);
                }
                else
                {
                    operation.Capture?.RecordRevalidationFailure();
                }
            }

            return;
        }

        if (managedHiddenWindows.Remove(snapshot.Handle))
        {
            if (windowService.RevalidateWindow(snapshot))
            {
                windowService.SetVisible(snapshot.Handle, visible: true);
            }
            else
            {
                operation.Capture?.RecordRevalidationFailure();
            }
        }
    }
}
