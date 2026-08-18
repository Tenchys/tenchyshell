namespace MinimalShell.Core.Windows;

public interface IWindowService
{
    WindowCloseResult CloseActiveWindow();

    WindowOperationResult MoveActiveWindow(int deltaX, int deltaY);

    WindowOperationResult ResizeActiveWindow(int deltaWidth, int deltaHeight);

    WindowOperationResult MaximizeActiveWindow();

    WindowOperationResult RestoreActiveWindow();

    WindowOperationResult FocusActiveWindow();
}
