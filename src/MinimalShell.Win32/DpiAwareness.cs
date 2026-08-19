using System.Runtime.InteropServices;

namespace MinimalShell.Win32;

public static class DpiAwareness
{
    public static bool TryEnablePerMonitorV2(out string? error)
    {
        error = null;

        try
        {
            if (NativeMethods.SetProcessDpiAwarenessContext(
                    NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                return true;
            }

            error = $"Código Win32: {Marshal.GetLastWin32Error()}.";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
