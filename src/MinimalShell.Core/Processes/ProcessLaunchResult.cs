namespace MinimalShell.Core.Processes;

public sealed class ProcessLaunchResult
{
    private ProcessLaunchResult(bool succeeded, int? processId, string? error)
    {
        Succeeded = succeeded;
        ProcessId = processId;
        Error = error;
    }

    public bool Succeeded { get; }

    public int? ProcessId { get; }

    public string? Error { get; }

    public static ProcessLaunchResult Success(int? processId) => new(true, processId, null);

    public static ProcessLaunchResult Failure(string error) => new(false, null, error);
}
