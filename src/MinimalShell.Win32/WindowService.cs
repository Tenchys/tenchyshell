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
}
