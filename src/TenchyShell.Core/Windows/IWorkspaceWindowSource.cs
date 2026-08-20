namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowSource
{
    int CurrentWorkspace { get; }

    IReadOnlyList<IntPtr> GetCurrentWorkspaceWindows();

    void Refresh();
}
