using MinimalShell.Core.Windows;

namespace MinimalShell.Workspaces;

public sealed class WorkspaceManager
{
    public const int FirstWorkspace = 1;
    public const int LastWorkspace = 9;

    private readonly IWorkspaceWindowService windowService;
    private readonly Dictionary<int, HashSet<IntPtr>> windowsByWorkspace =
        Enumerable.Range(FirstWorkspace, LastWorkspace)
            .ToDictionary(workspace => workspace, _ => new HashSet<IntPtr>());
    private int currentWorkspace = FirstWorkspace;

    public WorkspaceManager(IWorkspaceWindowService windowService)
    {
        this.windowService = windowService;
    }

    public int CurrentWorkspace => currentWorkspace;

    public void Refresh()
    {
        foreach (var windowHandle in windowService.GetVisibleTopLevelWindows())
        {
            if (!windowsByWorkspace.Values.Any(windows => windows.Contains(windowHandle)))
            {
                windowsByWorkspace[currentWorkspace].Add(windowHandle);
            }
        }
    }

    public WorkspaceOperationResult SwitchTo(int workspace)
    {
        if (!IsValidWorkspace(workspace))
        {
            return WorkspaceOperationResult.Failure($"El workspace {workspace} no es válido. Usa un valor entre 1 y 9.");
        }

        Refresh();

        if (workspace == currentWorkspace)
        {
            return WorkspaceOperationResult.Success();
        }

        foreach (var windowHandle in windowsByWorkspace[currentWorkspace])
        {
            windowService.SetVisible(windowHandle, visible: false);
        }

        currentWorkspace = workspace;
        var targetWindows = windowsByWorkspace[currentWorkspace].ToArray();

        foreach (var windowHandle in targetWindows)
        {
            windowService.SetVisible(windowHandle, visible: true);
        }

        var foregroundWindow = targetWindows.FirstOrDefault();
        if (foregroundWindow != IntPtr.Zero)
        {
            windowService.Focus(foregroundWindow);
        }

        return WorkspaceOperationResult.Success();
    }

    public WorkspaceOperationResult MoveForegroundTo(int workspace)
    {
        if (!IsValidWorkspace(workspace))
        {
            return WorkspaceOperationResult.Failure($"El workspace {workspace} no es válido. Usa un valor entre 1 y 9.");
        }

        var foregroundWindow = windowService.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return WorkspaceOperationResult.Failure("No hay una ventana activa para mover.");
        }

        Refresh();
        foreach (var windows in windowsByWorkspace.Values)
        {
            windows.Remove(foregroundWindow);
        }

        windowsByWorkspace[workspace].Add(foregroundWindow);

        if (workspace == currentWorkspace)
        {
            windowService.SetVisible(foregroundWindow, visible: true);
            windowService.Focus(foregroundWindow);
        }
        else
        {
            windowService.SetVisible(foregroundWindow, visible: false);
        }

        return WorkspaceOperationResult.Success();
    }

    private static bool IsValidWorkspace(int workspace) => workspace is >= FirstWorkspace and <= LastWorkspace;
}
