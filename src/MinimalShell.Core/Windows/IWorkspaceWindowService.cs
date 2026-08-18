namespace MinimalShell.Core.Windows;

public interface IWorkspaceWindowService
{
    IReadOnlyList<IntPtr> GetVisibleTopLevelWindows();

    IntPtr GetForegroundWindow();

    void SetVisible(IntPtr windowHandle, bool visible);

    bool Focus(IntPtr windowHandle);
}
