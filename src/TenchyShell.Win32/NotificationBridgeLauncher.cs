using System.Diagnostics;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;

namespace TenchyShell.Win32;

/// <summary>Activa el bridge empaquetado mediante su alias MSIX, sin Explorer.</summary>
public static class NotificationBridgeLauncher
{
    public const string ExecutionAlias = "TenchyShellNotificationBridge.exe";

    public static void Start(ILogger logger, LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        benchmarkRecorder?.Record("process_launch_requested", new { command = ExecutionAlias, arguments = Array.Empty<string>(), source = "notification_bridge" });
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ExecutionAlias,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            logger.Info("Se solicitó la activación del bridge MSIX de notificaciones.");
            benchmarkRecorder?.Record("process_launch_completed", new { command = ExecutionAlias, arguments = Array.Empty<string>(), pid = process?.Id, succeeded = true, error = (string?)null, source = "notification_bridge" });
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo activar el bridge MSIX. Instálalo para este usuario o desactiva [notifications].enabled.", exception);
            benchmarkRecorder?.Record("process_launch_completed", new { command = ExecutionAlias, arguments = Array.Empty<string>(), pid = (int?)null, succeeded = false, error = exception.Message, source = "notification_bridge" });
        }
    }
}
