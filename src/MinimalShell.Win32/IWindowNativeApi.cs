namespace MinimalShell.Win32;

public interface IWindowNativeApi
{
    IntPtr GetForegroundWindow();

    bool IsWindow(IntPtr windowHandle);

    uint GetWindowProcessId(IntPtr windowHandle);

    bool PostCloseMessage(IntPtr windowHandle, out int errorCode);
}
