namespace TenchyShell.Core.Windows;

public sealed class WindowOperationResult
{
    private WindowOperationResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public string? Error { get; }

    public static WindowOperationResult Success() => new(true, null);

    public static WindowOperationResult Failure(string error) => new(false, error);
}
