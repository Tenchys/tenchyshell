namespace TenchyShell.Core.Windows;

public readonly record struct WindowMonitor(
    string Id,
    bool IsPrimary,
    WindowRect WorkArea,
    WindowRect MonitorArea = default);
