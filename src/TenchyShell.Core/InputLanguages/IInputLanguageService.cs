namespace TenchyShell.Core.InputLanguages;

/// <summary>Consulta y solicita cambios de métodos de entrada ya habilitados por Windows.</summary>
public interface IInputLanguageService
{
    InputLanguageSnapshot GetSnapshot(nint targetWindow);

    InputLanguageOperationResult Activate(nint layoutHandle, nint targetWindow);
}
