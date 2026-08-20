namespace TenchyShell.Workspaces;

public sealed class WorkspaceOperationResult
{
    private WorkspaceOperationResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public string? Error { get; }

    public static WorkspaceOperationResult Success() => new(true, null);

    public static WorkspaceOperationResult Failure(string error) => new(false, error);
}
