namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowService
{
    IReadOnlyList<IntPtr> GetVisibleTopLevelWindows();

    string GetWindowTitle(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    void SetVisible(IntPtr windowHandle, bool visible);

    WorkspaceFocusResult Focus(IntPtr windowHandle);

    /// <summary>Obtiene metadatos sin modificar el estado de la ventana.</summary>
    WorkspaceWindowMetadata GetWindowMetadata(IntPtr windowHandle) =>
        new(
            windowHandle != IntPtr.Zero,
            windowHandle != IntPtr.Zero,
            false,
            GetWindowTitle(windowHandle),
            true);

    /// <summary>Restaura una ventana minimizada y solicita su foco.</summary>
    WorkspaceFocusResult RestoreAndFocus(IntPtr windowHandle) => Focus(windowHandle);
}

public readonly record struct WorkspaceWindowMetadata(
    bool Exists,
    bool IsVisible,
    bool IsMinimized,
    string Title,
    bool IsOperable);

public readonly record struct WorkspaceFocusResult(bool Succeeded, WorkspaceFocusFailure Failure)
{
    public static WorkspaceFocusResult Success() => new(true, WorkspaceFocusFailure.None);

    public static WorkspaceFocusResult Failed(WorkspaceFocusFailure failure) => new(false, failure);
}

public enum WorkspaceFocusFailure
{
    None,
    InvalidWindow,
    AccessDenied,
    WindowsRejected
}
