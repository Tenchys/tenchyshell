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
        var operation = workspaceSource.CaptureCurrentWorkspaceSnapshot("minimized");
        var workspace = operation.CurrentWorkspace;
        var entries = operation.CurrentWindows
            .Where(snapshot =>
            {
                var managedHidden = visibilityService?.IsManagedHidden(snapshot.Handle) == true;
                return snapshot.Exists &&
                       snapshot.IsMinimized &&
                       (snapshot.IsVisible || managedHidden) &&
                       (snapshot.IsOperable || managedHidden) &&
                       !string.IsNullOrWhiteSpace(snapshot.Title);
            })
            .Select(snapshot => new MinimizedWindowEntry(snapshot.Handle, snapshot.Title, workspace));

        // Fuera de WorkspaceManager se conserva el contrato de dobles antiguos.
        if (operation.Capture is null && operation.CurrentWindows.Count == 0)
        {
            workspaceSource.Refresh();
            entries = workspaceSource.GetCurrentWorkspaceWindows().Select(windowHandle =>
            {
                var metadata = windowService.GetWindowMetadata(windowHandle);
                return (windowHandle, metadata, managedHidden: visibilityService?.IsManagedHidden(windowHandle) == true);
            }).Where(item => item.metadata.Exists && item.metadata.IsMinimized &&
                (item.metadata.IsVisible || item.managedHidden) && (item.metadata.IsOperable || item.managedHidden) &&
                !string.IsNullOrWhiteSpace(item.metadata.Title))
                .Select(item => new MinimizedWindowEntry(item.windowHandle, item.metadata.Title, workspace));
        }

        return new MinimizedWindowMenuState(entries, workspace);
    }

    public WorkspaceFocusResult RestoreAndFocus(MinimizedWindowEntry entry)
    {
        if (entry.Handle == IntPtr.Zero || entry.Workspace != workspaceSource.CurrentWorkspace)
        {
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        var operation = workspaceSource.CaptureCurrentWorkspaceSnapshot("minimized_restore");
        if (operation.Capture is null)
        {
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

            var legacyMetadata = windowService.GetWindowMetadata(entry.Handle);
            if (!legacyMetadata.Exists || !legacyMetadata.IsVisible || !legacyMetadata.IsMinimized || !legacyMetadata.IsOperable)
            {
                visibilityService?.Forget(entry.Handle);
                return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
            }

            return windowService.RestoreAndFocus(entry.Handle);
        }

        var snapshot = operation.CurrentWindows.FirstOrDefault(item => item.Handle == entry.Handle);
        if (snapshot.Handle == IntPtr.Zero)
        {
            visibilityService?.Forget(entry.Handle);
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        var wasManagedHidden = visibilityService?.IsManagedHidden(entry.Handle) == true;
        if (wasManagedHidden)
        {
            if (!TryRevalidate(operation, snapshot)) return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
            visibilityService!.Forget(entry.Handle);
            windowService.SetVisible(entry.Handle, visible: true);
        }

        if (!TryRevalidate(operation, snapshot) || !snapshot.Exists || !snapshot.IsMinimized ||
            (!snapshot.IsVisible && !wasManagedHidden) ||
            (!snapshot.IsOperable && !wasManagedHidden))
        {
            visibilityService?.Forget(entry.Handle);
            return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        }

        if (!TryRevalidate(operation, snapshot)) return WorkspaceFocusResult.Failed(WorkspaceFocusFailure.InvalidWindow);
        return windowService.RestoreAndFocus(snapshot);
    }

    private bool TryRevalidate(WorkspaceOperationSnapshot operation, WorkspaceWindowSnapshot snapshot)
    {
        var valid = windowService.RevalidateWindow(snapshot);
        if (!valid) operation.Capture?.RecordRevalidationFailure();
        return valid;
    }
}
