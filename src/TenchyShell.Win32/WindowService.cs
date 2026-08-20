using System.ComponentModel;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

public sealed class WindowService : IWindowService
{
    private readonly IWindowNativeApi nativeApi;
    private readonly uint currentProcessId;

    public WindowService(IWindowNativeApi? nativeApi = null, uint? currentProcessId = null)
    {
        this.nativeApi = nativeApi ?? new NativeWindowApi();
        this.currentProcessId = currentProcessId ?? (uint)Environment.ProcessId;
    }

    public WindowCloseResult CloseActiveWindow()
    {
        var windowHandle = nativeApi.GetForegroundWindow();

        if (windowHandle == IntPtr.Zero || !nativeApi.IsWindow(windowHandle))
        {
            return WindowCloseResult.Failure("No hay una ventana activa válida para cerrar.");
        }

        var processId = nativeApi.GetWindowProcessId(windowHandle);

        if (processId == currentProcessId)
        {
            return WindowCloseResult.Failure("La ventana activa pertenece a TenchyShell y no se cerrará.");
        }

        if (!nativeApi.PostCloseMessage(windowHandle, out var error))
        {
            return WindowCloseResult.Failure(
                $"Windows no aceptó la solicitud de cierre (código Win32: {error}, {new Win32Exception(error).Message}).");
        }

        return WindowCloseResult.Success();
    }

    public WindowOperationResult MoveActiveWindow(int deltaX, int deltaY)
    {
        if (!TryGetActiveWindow(out var windowHandle, out var error))
        {
            return WindowOperationResult.Failure(error!);
        }

        if (!nativeApi.TryGetWindowRect(windowHandle, out var windowRect) ||
            !nativeApi.TryGetWorkArea(windowHandle, out var workArea))
        {
            return WindowOperationResult.Failure("No se pudo obtener la geometría del monitor o de la ventana activa.");
        }

        var left = Clamp(windowRect.Left + deltaX, workArea.Left, workArea.Right - windowRect.Width);
        var top = Clamp(windowRect.Top + deltaY, workArea.Top, workArea.Bottom - windowRect.Height);
        return SetWindowPosition(windowHandle, new WindowRect(left, top, left + windowRect.Width, top + windowRect.Height));
    }

    public WindowOperationResult ResizeActiveWindow(int deltaWidth, int deltaHeight)
    {
        if (!TryGetActiveWindow(out var windowHandle, out var error))
        {
            return WindowOperationResult.Failure(error!);
        }

        if (!nativeApi.TryGetWindowRect(windowHandle, out var windowRect) ||
            !nativeApi.TryGetWorkArea(windowHandle, out var workArea))
        {
            return WindowOperationResult.Failure("No se pudo obtener la geometría del monitor o de la ventana activa.");
        }

        var width = Clamp(windowRect.Width + deltaWidth, 160, workArea.Width);
        var height = Clamp(windowRect.Height + deltaHeight, 120, workArea.Height);
        var right = Math.Min(windowRect.Left + width, workArea.Right);
        var bottom = Math.Min(windowRect.Top + height, workArea.Bottom);
        var left = Math.Max(workArea.Left, right - width);
        var top = Math.Max(workArea.Top, bottom - height);
        return SetWindowPosition(windowHandle, new WindowRect(left, top, right, bottom));
    }

    public WindowOperationResult MaximizeActiveWindow() => ShowActiveWindow(NativeMethods.SW_MAXIMIZE);

    public WindowOperationResult RestoreActiveWindow() => ShowActiveWindow(NativeMethods.SW_RESTORE);

    public WindowOperationResult FocusActiveWindow()
    {
        if (!TryGetActiveWindow(out var windowHandle, out var error))
        {
            return WindowOperationResult.Failure(error!);
        }

        return nativeApi.FocusWindow(windowHandle)
            ? WindowOperationResult.Success()
            : WindowOperationResult.Failure("Windows no permitió enfocar la ventana activa.");
    }

    public bool TryGetActiveWorkArea(out WindowRect workArea, out string? error)
    {
        workArea = default;

        if (!TryGetActiveMonitor(out var monitor, out error))
        {
            return false;
        }

        workArea = monitor.WorkArea;
        return true;
    }

    public bool TryGetActiveMonitor(out WindowMonitor monitor, out string? error)
    {
        monitor = default;

        if (!TryGetActiveWindow(out var windowHandle, out error))
        {
            return false;
        }

        if (!nativeApi.TryGetMonitor(windowHandle, out monitor))
        {
            error = "No se pudo obtener el monitor de la ventana activa.";
            return false;
        }

        return true;
    }

    public WindowOperationResult PlaceActiveWindow(WindowRect targetRect)
    {
        if (!TryGetActiveWindow(out var windowHandle, out var error))
        {
            return WindowOperationResult.Failure(error!);
        }

        return PlaceWindow(windowHandle, targetRect);
    }

    public WindowOperationResult PlaceWindow(IntPtr windowHandle, WindowRect targetRect)
    {
        if (targetRect.Width <= 0 || targetRect.Height <= 0)
        {
            return WindowOperationResult.Failure("La zona calculada no tiene un tamaño válido.");
        }

        if (windowHandle == IntPtr.Zero || !nativeApi.IsWindow(windowHandle))
        {
            return WindowOperationResult.Failure("La ventana objetivo no es válida.");
        }

        if (nativeApi.GetWindowProcessId(windowHandle) == currentProcessId)
        {
            return WindowOperationResult.Failure("La ventana objetivo pertenece a TenchyShell y no se modificará.");
        }

        var centerX = targetRect.Left + targetRect.Width / 2;
        var centerY = targetRect.Top + targetRect.Height / 2;
        if (!nativeApi.TryGetMonitorAtPoint(centerX, centerY, out var targetMonitor) &&
            !nativeApi.TryGetMonitor(windowHandle, out targetMonitor))
        {
            return WindowOperationResult.Failure("No se pudo obtener el monitor destino de la ventana.");
        }

        var workArea = targetMonitor.WorkArea;
        var width = Math.Min(targetRect.Width, workArea.Width);
        var height = Math.Min(targetRect.Height, workArea.Height);
        var left = Clamp(targetRect.Left, workArea.Left, workArea.Right - width);
        var top = Clamp(targetRect.Top, workArea.Top, workArea.Bottom - height);
        var clampedRect = new WindowRect(left, top, left + width, top + height);

        // ShowWindow devuelve el estado anterior de la ventana, no un indicador fiable de error.
        nativeApi.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
        return SetWindowPosition(windowHandle, clampedRect);
    }

    private WindowOperationResult ShowActiveWindow(uint command)
    {
        if (!TryGetActiveWindow(out var windowHandle, out var error))
        {
            return WindowOperationResult.Failure(error!);
        }

        return nativeApi.ShowWindow(windowHandle, command)
            ? WindowOperationResult.Success()
            : WindowOperationResult.Failure("Windows no permitió cambiar el estado de la ventana activa.");
    }

    private WindowOperationResult SetWindowPosition(IntPtr windowHandle, WindowRect windowRect) => nativeApi.SetWindowPosition(
        windowHandle,
        windowRect)
        ? WindowOperationResult.Success()
        : WindowOperationResult.Failure("Windows no permitió cambiar la geometría de la ventana activa.");

    public bool TryGetActiveWindow(out IntPtr windowHandle, out string? error)
    {
        return TryValidateWindow(nativeApi.GetForegroundWindow(), out windowHandle, out error);
    }

    public bool TryGetWindowAtPoint(int x, int y, out IntPtr windowHandle, out string? error)
    {
        return TryValidateWindow(nativeApi.GetWindowFromPoint(x, y), out windowHandle, out error);
    }

    private bool TryValidateWindow(IntPtr candidate, out IntPtr windowHandle, out string? error)
    {
        windowHandle = candidate;
        error = null;

        if (windowHandle == IntPtr.Zero || !nativeApi.IsWindow(windowHandle))
        {
            error = "No hay una ventana activa válida.";
            return false;
        }

        if (nativeApi.GetWindowProcessId(windowHandle) == currentProcessId)
        {
            error = "La ventana activa pertenece a TenchyShell y no se operará sobre ella.";
            return false;
        }

        return true;
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Min(Math.Max(value, minimum), maximum);
}
