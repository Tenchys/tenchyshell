namespace TenchyShell.Core.Applications;

/// <summary>Contexto COM usado para activar una aplicación empaquetada.</summary>
public enum PackagedApplicationActivationContext
{
    InProcess,
    LocalServer
}

/// <summary>Selección de contextos para la prueba comparativa explícita.</summary>
public enum PackagedApplicationActivationContextSelection
{
    InProcess,
    LocalServer,
    Both
}
