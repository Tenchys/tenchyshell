using TenchyShell.Core.Processes;

namespace TenchyShell.Core.Applications;

/// <summary>
/// Resultado de una activación MSIX/UWP. Conserva el resultado de lanzamiento
/// público y añade los datos COM necesarios para informar fallos de forma segura.
/// </summary>
public sealed record PackagedApplicationActivationResult(
    ProcessLaunchResult LaunchResult,
    int HResult,
    PackagedActivationDiagnosticStage Stage,
    string Message,
    bool TimedOut)
{
    public bool Succeeded => LaunchResult.Succeeded;

    public int? ProcessId => LaunchResult.ProcessId;

    public string? Error => LaunchResult.Error;

    public string HResultHex => $"0x{unchecked((uint)HResult):X8}";

    public static PackagedApplicationActivationResult Success(
        int? processId,
        int hresult,
        PackagedActivationDiagnosticStage stage,
        string message) => new(ProcessLaunchResult.Success(processId), hresult, stage, message, TimedOut: false);

    public static PackagedApplicationActivationResult Failure(
        string error,
        int hresult,
        PackagedActivationDiagnosticStage stage,
        string message,
        bool timedOut = false) => new(ProcessLaunchResult.Failure(error), hresult, stage, message, timedOut);
}
