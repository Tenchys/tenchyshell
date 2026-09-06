namespace TenchyShell.Core.Applications;

/// <summary>Etapa más reciente alcanzada por un intento de activación diagnóstica.</summary>
public enum PackagedActivationDiagnosticStage
{
    InputValidation,
    CoInitializeEx,
    CoCreateInstance,
    ActivateApplication
}
