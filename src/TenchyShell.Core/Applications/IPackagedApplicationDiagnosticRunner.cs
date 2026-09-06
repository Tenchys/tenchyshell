namespace TenchyShell.Core.Applications;

/// <summary>Ejecuta una activación comparativa sin afectar el launcher normal.</summary>
public interface IPackagedApplicationDiagnosticRunner
{
    PackagedActivationDiagnosticResult Diagnose(
        string appUserModelId,
        PackagedApplicationActivationContext context,
        string explorerShellState);
}
