using System.Runtime.InteropServices;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

public sealed class MonitorService
{
    public IReadOnlyList<WindowMonitor> GetAll()
    {
        var monitors = new List<WindowMonitor>();
        NativeMethods.MonitorEnumProc callback = (
            IntPtr monitorHandle,
            IntPtr deviceContext,
            ref NativeMethods.Rect monitorRectangle,
            IntPtr data) =>
        {
            if (TryGetMonitor(monitorHandle, out var monitor))
            {
                monitors.Add(monitor);
            }

            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            return Array.Empty<WindowMonitor>();
        }

        return monitors
            .OrderByDescending(monitor => monitor.IsPrimary)
            .ThenBy(monitor => monitor.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetMonitor(IntPtr monitorHandle, out WindowMonitor monitor)
    {
        monitor = default;
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty
        };

        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info))
        {
            return false;
        }

        monitor = new WindowMonitor(
            info.DeviceName,
            (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            new WindowRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            new WindowRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
        return monitor.WorkArea.Width > 0 && monitor.WorkArea.Height > 0;
    }
}
