namespace TenchyShell.Core.Applications;

/// <summary>Resultado de un intento diagnóstico, distinto al lanzamiento normal.</summary>
public sealed record PackagedActivationDiagnosticResult(
    string AppUserModelId,
    PackagedApplicationActivationContext Context,
    string ExplorerShellState,
    bool Succeeded,
    int? ProcessId,
    int HResult,
    PackagedActivationDiagnosticStage Stage,
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    public string HResultHex => $"0x{unchecked((uint)HResult):X8}";
}
