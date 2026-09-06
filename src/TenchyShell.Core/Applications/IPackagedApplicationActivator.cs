namespace TenchyShell.Core.Applications;

/// <summary>Activa una aplicación empaquetada mediante su AppUserModelId.</summary>
public interface IPackagedApplicationActivator
{
    PackagedApplicationActivationResult Activate(ApplicationEntry application);
}
