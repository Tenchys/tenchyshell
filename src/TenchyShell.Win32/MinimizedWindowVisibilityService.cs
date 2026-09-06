using System.ComponentModel;
using System.Runtime.InteropServices;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;

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

        workspaceSource.Refresh();
        foreach (var windowHandle in workspaceSource.GetCurrentWorkspaceWindows())
        {
            SynchronizeWindow(windowHandle, workspaceAlreadyRefreshed: true);
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
        messageLoop.Post(() => SynchronizeWindow(windowHandle, workspaceAlreadyRefreshed: false));
    }

    private void SynchronizeWindow(IntPtr windowHandle, bool workspaceAlreadyRefreshed)
    {
        if (isDisposed) return;

        if (!workspaceAlreadyRefreshed) workspaceSource.Refresh();
        if (!workspaceSource.GetCurrentWorkspaceWindows().Contains(windowHandle)) return;

        var metadata = windowService.GetWindowMetadata(windowHandle);
        if (!metadata.Exists)
        {
            managedHiddenWindows.Remove(windowHandle);
            return;
        }

        if (metadata.IsMinimized)
        {
            if (!managedHiddenWindows.Contains(windowHandle) && metadata.IsVisible && metadata.IsOperable)
            {
                windowService.SetVisible(windowHandle, visible: false);
                managedHiddenWindows.Add(windowHandle);
            }

            return;
        }

        if (managedHiddenWindows.Remove(windowHandle))
        {
            windowService.SetVisible(windowHandle, visible: true);
        }
    }
}
