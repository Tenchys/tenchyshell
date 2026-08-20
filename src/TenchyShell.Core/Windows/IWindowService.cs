namespace TenchyShell.Core.Windows;

public interface IWindowService
{
    WindowCloseResult CloseActiveWindow();

    WindowOperationResult MoveActiveWindow(int deltaX, int deltaY);

    WindowOperationResult ResizeActiveWindow(int deltaWidth, int deltaHeight);

    WindowOperationResult MaximizeActiveWindow();

    WindowOperationResult RestoreActiveWindow();

    WindowOperationResult FocusActiveWindow();

    bool TryGetActiveWorkArea(out WindowRect workArea, out string? error);

    bool TryGetActiveMonitor(out WindowMonitor monitor, out string? error);

    WindowOperationResult PlaceActiveWindow(WindowRect targetRect);

    bool TryGetActiveWindow(out IntPtr windowHandle, out string? error);

    bool TryGetWindowAtPoint(int x, int y, out IntPtr windowHandle, out string? error);

    WindowOperationResult PlaceWindow(IntPtr windowHandle, WindowRect targetRect);
}
