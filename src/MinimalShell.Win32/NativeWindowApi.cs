using System.Runtime.InteropServices;
using MinimalShell.Core.Windows;

namespace MinimalShell.Win32;

internal sealed class NativeWindowApi : IWindowNativeApi
{
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

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
        var monitor = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            workArea = default;
            return false;
        }

        workArea = new WindowRect(
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right,
            monitorInfo.Work.Bottom);
        return true;
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
}
