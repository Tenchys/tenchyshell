using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

/// <summary>Selecciona el área usable sin modificar la configuración global de Windows.</summary>
public sealed class DesktopAreaPolicy
{
    private bool useMonitorArea;

    public event Action? Changed;

    public bool UseMonitorArea
    {
        get => useMonitorArea;
        set
        {
            if (useMonitorArea == value)
            {
                return;
            }

            useMonitorArea = value;
            Changed?.Invoke();
        }
    }

    public WindowRect GetArea(WindowMonitor monitor) =>
        UseMonitorArea && monitor.MonitorArea.Width > 0 && monitor.MonitorArea.Height > 0
            ? monitor.MonitorArea
            : monitor.WorkArea;
}
