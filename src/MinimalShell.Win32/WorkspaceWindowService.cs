using MinimalShell.Core.Windows;

namespace MinimalShell.Win32;

public sealed class WorkspaceWindowService : IWorkspaceWindowService
{
    private readonly uint currentProcessId = (uint)Environment.ProcessId;

    public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows()
    {
        var windows = new List<IntPtr>();

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(windowHandle))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == currentProcessId || IsShellWindow(windowHandle))
            {
                return true;
            }

            windows.Add(windowHandle);
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public void SetVisible(IntPtr windowHandle, bool visible)
    {
        NativeMethods.ShowWindow(windowHandle, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
    }

    public bool Focus(IntPtr windowHandle) => NativeMethods.SetForegroundWindow(windowHandle);

    private static bool IsShellWindow(IntPtr windowHandle)
    {
        var className = NativeMethods.GetWindowClassName(windowHandle);
        return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
    }
}
