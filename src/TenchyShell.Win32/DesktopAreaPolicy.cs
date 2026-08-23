using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

/// <summary>Selecciona el área usable sin modificar la configuración global de Windows.</summary>
public sealed class DesktopAreaPolicy
{
    public bool UseMonitorArea { get; set; }

    public WindowRect GetArea(WindowMonitor monitor) =>
        UseMonitorArea && monitor.MonitorArea.Width > 0 && monitor.MonitorArea.Height > 0
            ? monitor.MonitorArea
            : monitor.WorkArea;
}
