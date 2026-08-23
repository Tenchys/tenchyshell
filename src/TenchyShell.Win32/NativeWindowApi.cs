using System.Runtime.InteropServices;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

internal sealed class NativeWindowApi : IWindowNativeApi
{
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public IntPtr GetWindowFromPoint(int x, int y)
    {
        var point = new NativeMethods.Point { X = x, Y = y };
        var windowHandle = NativeMethods.WindowFromPoint(point);
        var rootWindow = windowHandle == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(windowHandle, NativeMethods.GA_ROOT);

        return rootWindow == IntPtr.Zero ? windowHandle : rootWindow;
    }

    public bool IsWindow(IntPtr windowHandle) => NativeMethods.IsWindow(windowHandle);

    public uint GetWindowProcessId(IntPtr windowHandle)
    {
        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }

    public bool PostCloseMessage(IntPtr windowHandle, out int errorCode)
    {
        var succeeded = NativeMethods.PostMessage(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public bool TryGetWindowRect(IntPtr windowHandle, out WindowRect windowRect)
    {
        if (NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            windowRect = new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }

        windowRect = default;
        return false;
    }

    public bool TryGetWorkArea(IntPtr windowHandle, out WindowRect workArea)
    {
        if (!TryGetMonitor(windowHandle, out var monitor))
        {
            workArea = default;
            return false;
        }

        workArea = monitor.WorkArea;
        return true;
    }

    public bool TryGetMonitor(IntPtr windowHandle, out WindowMonitor monitor)
    {
        var monitorHandle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return TryGetMonitorHandle(monitorHandle, out monitor);
    }

    public bool TryGetMonitorAtPoint(int x, int y, out WindowMonitor monitor)
    {
        var monitorHandle = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point { X = x, Y = y },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        return TryGetMonitorHandle(monitorHandle, out monitor);
    }

    public bool SetWindowPosition(IntPtr windowHandle, WindowRect windowRect) => NativeMethods.SetWindowPos(
        windowHandle,
        IntPtr.Zero,
        windowRect.Left,
        windowRect.Top,
        windowRect.Width,
        windowRect.Height,
        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public bool ShowWindow(IntPtr windowHandle, uint command) => NativeMethods.ShowWindow(windowHandle, command);

    public bool FocusWindow(IntPtr windowHandle) => NativeMethods.SetForegroundWindow(windowHandle);

    private static bool TryGetMonitorHandle(IntPtr monitorHandle, out WindowMonitor monitor)
    {
        monitor = default;

        var monitorInfo = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty
        };

        if (monitorHandle == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        monitor = new WindowMonitor(
            monitorInfo.DeviceName,
            (monitorInfo.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            new WindowRect(
                monitorInfo.Work.Left,
                monitorInfo.Work.Top,
                monitorInfo.Work.Right,
                monitorInfo.Work.Bottom),
            new WindowRect(
                monitorInfo.Monitor.Left,
                monitorInfo.Monitor.Top,
                monitorInfo.Monitor.Right,
                monitorInfo.Monitor.Bottom));
        return monitor.WorkArea.Width > 0 && monitor.WorkArea.Height > 0;
    }
}
