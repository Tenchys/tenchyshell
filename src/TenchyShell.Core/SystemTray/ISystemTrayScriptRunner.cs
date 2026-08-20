namespace TenchyShell.Core.SystemTray;

public interface ISystemTrayScriptRunner
{
    Task<SystemTrayScriptOutputResult> RunAsync(
        SystemTrayItemConfiguration item,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed record SystemTrayScriptOutputResult(
    bool Succeeded,
    SystemTrayScriptOutput? Output,
    string? Error)
{
    public static SystemTrayScriptOutputResult Success(SystemTrayScriptOutput output) =>
        new(true, output, null);

    public static SystemTrayScriptOutputResult Failure(string error) =>
        new(false, null, error);
}
