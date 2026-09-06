namespace TenchyShell.Core.Windows;

/// <summary>Estado de visibilidad de ventanas ocultadas por TenchyShell.</summary>
public interface IMinimizedWindowVisibilityService : IDisposable
{
    bool IsManagedHidden(IntPtr windowHandle);

    void Start();

    void Synchronize();

    void Forget(IntPtr windowHandle);
}
