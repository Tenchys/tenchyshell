using TenchyShell.Core.Windows;

namespace TenchyShell.Workspaces;

/// <summary>
/// Construye el listado acotado de ventanas minimizadas del workspace activo y
/// delega la operación Win32 de restauración/foco en el servicio de ventanas.
/// </summary>
public sealed class MinimizedWindowService : IMinimizedWindowService
{
    private readonly IWorkspaceWindowSource workspaceSource;
    private readonly IWorkspaceWindowService windowService;
    private readonly IMinimizedWindowVisibilityService? visibilityService;

    public MinimizedWindowService(
        IWorkspaceWindowSource workspaceSource,
        IWorkspaceWindowService windowService,
        IMinimizedWindowVisibilityService? visibilityService = null)
    {
        this.workspaceSource = workspaceSource;
        this.windowService = windowService;
        this.visibilityService = visibilityService;
    }

    public MinimizedWindowMenuState GetCurrentState()
    {
        workspaceSource.Refresh();
        var workspace = workspaceSource.CurrentWorkspace;
        var entries = workspaceSource.GetCurrentWorkspaceWindows()
            .Select(windowHandle =>
            {
                var metadata = windowService.GetWindowMetadata(windowHandle);
                return (windowHandle, metadata, managedHidden: visibilityService?.IsManagedHidden(windowHandle) == true);
            })
            .Where(item => item.metadata.Exists &&
                           item.metadata.IsMinimized &&
                           (item.metadata.IsVisible || item.managedHidden) &&
                           (item.metadata.IsOperable || item.managedHidden) &&
                           !string.IsNullOrWhiteSpace(item.metadata.Title))
            .Select(item => new MinimizedWindowEntry(item.windowHandle, item.metadata.Title, workspace));

        return new MinimizedWindowMenuState(entries, workspace);
    }

    public WorkspaceFocusResult RestoreAndFocus(MinimizedWindowEntry entry)
    {
        if (entry.Handle == IntPtr.Zero || entry.Workspace != workspaceSource.CurrentWorkspace)
        {
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        workspaceSource.Refresh();
        if (!workspaceSource.GetCurrentWorkspaceWindows().Contains(entry.Handle))
        {
            visibilityService?.Forget(entry.Handle);
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        if (visibilityService?.IsManagedHidden(entry.Handle) == true)
        {
            visibilityService.Forget(entry.Handle);
            windowService.SetVisible(entry.Handle, visible: true);
        }

        var metadata = windowService.GetWindowMetadata(entry.Handle);
        if (!metadata.Exists || !metadata.IsVisible || !metadata.IsMinimized || !metadata.IsOperable)
        {
            visibilityService?.Forget(entry.Handle);
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        return windowService.RestoreAndFocus(entry.Handle);
    }
}
