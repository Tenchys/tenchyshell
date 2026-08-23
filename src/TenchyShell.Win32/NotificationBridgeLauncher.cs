using System.Diagnostics;
using TenchyShell.Core.Logging;

namespace TenchyShell.Win32;

/// <summary>Activa el bridge empaquetado mediante su alias MSIX, sin Explorer.</summary>
public static class NotificationBridgeLauncher
{
    public const string ExecutionAlias = "TenchyShellNotificationBridge.exe";

    public static void Start(ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ExecutionAlias,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            logger.Info("Se solicitó la activación del bridge MSIX de notificaciones.");
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo activar el bridge MSIX. Instálalo para este usuario o desactiva [notifications].enabled.", exception);
        }
    }
}
