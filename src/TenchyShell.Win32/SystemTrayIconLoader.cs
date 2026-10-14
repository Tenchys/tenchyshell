namespace TenchyShell.Win32;

internal enum SystemTrayIconPathStatus
{
    NotConfigured,
    InvalidExtension,
    MissingFile,
    Loadable
}

/// <summary>Pure path checks used before the native icon load.</summary>
internal static class SystemTrayIconLoader
{
    internal static SystemTrayIconPathStatus ClassifyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return SystemTrayIconPathStatus.NotConfigured;
        if (!path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return SystemTrayIconPathStatus.InvalidExtension;
        return File.Exists(path) ? SystemTrayIconPathStatus.Loadable : SystemTrayIconPathStatus.MissingFile;
    }

    internal static Core.Diagnostics.SystemTrayIconLoadStatus ClassifyLoadResult(
        SystemTrayIconPathStatus pathStatus,
        IntPtr iconHandle) =>
        pathStatus switch
        {
            SystemTrayIconPathStatus.NotConfigured => Core.Diagnostics.SystemTrayIconLoadStatus.NotConfigured,
            SystemTrayIconPathStatus.Loadable when iconHandle != IntPtr.Zero => Core.Diagnostics.SystemTrayIconLoadStatus.Loaded,
            _ => pathStatus == SystemTrayIconPathStatus.Loadable
                ? Core.Diagnostics.SystemTrayIconLoadStatus.Failed
                : Core.Diagnostics.SystemTrayIconLoadStatus.InvalidPath
        };
}
