namespace TenchyShell.Core.Windows;

public sealed class WindowCloseResult
{
    private WindowCloseResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public string? Error { get; }

    public static WindowCloseResult Success() => new(true, null);

    public static WindowCloseResult Failure(string error) => new(false, error);
}
