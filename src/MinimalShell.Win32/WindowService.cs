using System.ComponentModel;
using MinimalShell.Core.Windows;

namespace MinimalShell.Win32;

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
            return WindowCloseResult.Failure("La ventana activa pertenece a MinimalShell y no se cerrará.");
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

    private bool TryGetActiveWindow(out IntPtr windowHandle, out string? error)
    {
        windowHandle = nativeApi.GetForegroundWindow();
        error = null;

        if (windowHandle == IntPtr.Zero || !nativeApi.IsWindow(windowHandle))
        {
            error = "No hay una ventana activa válida.";
            return false;
        }

        if (nativeApi.GetWindowProcessId(windowHandle) == currentProcessId)
        {
            error = "La ventana activa pertenece a MinimalShell y no se operará sobre ella.";
            return false;
        }

        return true;
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Min(Math.Max(value, minimum), maximum);
}
