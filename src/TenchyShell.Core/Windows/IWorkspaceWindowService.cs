namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowService
{
    IReadOnlyList<IntPtr> GetVisibleTopLevelWindows();

    string GetWindowTitle(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    void SetVisible(IntPtr windowHandle, bool visible);

    bool Focus(IntPtr windowHandle);
}
