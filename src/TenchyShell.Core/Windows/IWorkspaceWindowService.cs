namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowService
{
    IReadOnlyList<IntPtr> GetVisibleTopLevelWindows();

    string GetWindowTitle(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    void SetVisible(IntPtr windowHandle, bool visible);

    WorkspaceFocusResult Focus(IntPtr windowHandle);
}

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
