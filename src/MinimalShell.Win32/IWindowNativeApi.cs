namespace MinimalShell.Win32;

using MinimalShell.Core.Windows;

public interface IWindowNativeApi
{
    IntPtr GetForegroundWindow();

    bool IsWindow(IntPtr windowHandle);

    uint GetWindowProcessId(IntPtr windowHandle);

    bool PostCloseMessage(IntPtr windowHandle, out int errorCode);

    bool TryGetWindowRect(IntPtr windowHandle, out WindowRect windowRect);

    bool TryGetWorkArea(IntPtr windowHandle, out WindowRect workArea);

    bool SetWindowPosition(IntPtr windowHandle, WindowRect windowRect);

    bool ShowWindow(IntPtr windowHandle, uint command);

    bool FocusWindow(IntPtr windowHandle);
}
