namespace TenchyShell.Core.Windows;

public interface IWorkspaceWindowSource
{
    int CurrentWorkspace { get; }

    IReadOnlyList<IntPtr> GetCurrentWorkspaceWindows();

    WorkspaceOperationSnapshot CaptureCurrentWorkspaceSnapshot(string operation = "workspace") =>
        new(operation, CurrentWorkspace, new Dictionary<int, IReadOnlyList<WorkspaceWindowSnapshot>>());

    void Refresh();
}

/// <summary>Vista efímera de un workspace y sus HWND para una única operación.</summary>
public sealed class WorkspaceOperationSnapshot
{
    public WorkspaceOperationSnapshot(
        string operation,
        int currentWorkspace,
        IReadOnlyDictionary<int, IReadOnlyList<WorkspaceWindowSnapshot>> windowsByWorkspace,
        WorkspaceWindowCapture? capture = null)
    {
        Operation = operation;
        CurrentWorkspace = currentWorkspace;
        WindowsByWorkspace = windowsByWorkspace;
        Capture = capture;
    }

    public string Operation { get; }
    public int CurrentWorkspace { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<WorkspaceWindowSnapshot>> WindowsByWorkspace { get; }
    public WorkspaceWindowCapture? Capture { get; }

    public IReadOnlyList<WorkspaceWindowSnapshot> CurrentWindows =>
        WindowsByWorkspace.TryGetValue(CurrentWorkspace, out var windows) ? windows : Array.Empty<WorkspaceWindowSnapshot>();
}
