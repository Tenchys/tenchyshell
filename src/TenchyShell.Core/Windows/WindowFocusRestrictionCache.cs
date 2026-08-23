namespace TenchyShell.Core.Windows;

/// <summary>Recuerda durante la sesión las ventanas que Windows no permite enfocar.</summary>
public sealed class WindowFocusRestrictionCache
{
    private readonly Dictionary<IntPtr, string> restrictedTitlesByHandle = new();

    public void Remember(IntPtr windowHandle, string title, WorkspaceFocusFailure failure)
    {
        if (windowHandle == IntPtr.Zero || failure != WorkspaceFocusFailure.AccessDenied) return;
        restrictedTitlesByHandle[windowHandle] = title;
    }

    public bool IsRestricted(IntPtr windowHandle, string title) =>
        restrictedTitlesByHandle.TryGetValue(windowHandle, out var rememberedTitle) &&
        string.Equals(rememberedTitle, title, StringComparison.Ordinal);

    public void Reconcile(IEnumerable<WindowSwitcherItem> windows)
    {
        var currentTitles = windows
            .Where(window => window.Handle != IntPtr.Zero)
            .GroupBy(window => window.Handle)
            .ToDictionary(group => group.Key, group => group.First().Title);

        foreach (var (windowHandle, title) in restrictedTitlesByHandle.ToArray())
        {
            if (!currentTitles.TryGetValue(windowHandle, out var currentTitle) ||
                !string.Equals(title, currentTitle, StringComparison.Ordinal))
            {
                restrictedTitlesByHandle.Remove(windowHandle);
            }
        }
    }
}
