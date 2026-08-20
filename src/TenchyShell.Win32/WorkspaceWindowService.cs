using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

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

            if (!IsSelectableWindow(windowHandle))
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

    public string GetWindowTitle(IntPtr windowHandle) => NativeMethods.GetWindowTitle(windowHandle);

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

    private static bool IsSelectableWindow(IntPtr windowHandle)
    {
        if (NativeMethods.GetWindow(windowHandle, NativeMethods.GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((extendedStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (!NativeMethods.HasWindowTitle(windowHandle))
        {
            return false;
        }

        return !NativeMethods.IsWindowCloaked(windowHandle);
    }
}
