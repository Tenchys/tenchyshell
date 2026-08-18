using System.Runtime.InteropServices;

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
}
