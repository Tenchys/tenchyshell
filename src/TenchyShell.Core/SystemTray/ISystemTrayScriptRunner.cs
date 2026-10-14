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
    string? Error,
    SystemTrayScriptExecutionOutcome Outcome = SystemTrayScriptExecutionOutcome.ExecutionError)
{
    public static SystemTrayScriptOutputResult Success(SystemTrayScriptOutput output) =>
        new(true, output, null, SystemTrayScriptExecutionOutcome.ValidOutput);

    public static SystemTrayScriptOutputResult Failure(
        string error,
        SystemTrayScriptExecutionOutcome outcome = SystemTrayScriptExecutionOutcome.ExecutionError) =>
        new(false, null, error, outcome);
}

/// <summary>
/// Safe, command-free classification of a periodic script outcome.
/// </summary>
public enum SystemTrayScriptExecutionOutcome
{
    ValidOutput,
    StartFailed,
    ProcessError,
    InvalidOutput,
    Cancelled,
    TimedOut,
    ExecutionError
}
