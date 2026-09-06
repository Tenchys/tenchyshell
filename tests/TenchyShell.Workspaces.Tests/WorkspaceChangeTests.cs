using TenchyShell.Core.Windows;
using TenchyShell.Workspaces;
using Xunit;

namespace TenchyShell.Workspaces.Tests;

public sealed class WorkspaceChangeTests
{
    [Fact]
    public void WorkspaceManagerRaisesStateChangedAfterAWorkspaceSwitch()
    {
        var manager = new WorkspaceManager(new FakeWorkspaceWindowService());
        var changes = 0;
        manager.StateChanged += (_, _) => changes++;

        Assert.True(manager.SwitchTo(2).Succeeded);
        Assert.Equal(1, changes);
        Assert.Equal(2, manager.CurrentWorkspace);
    }

    private sealed class FakeWorkspaceWindowService : IWorkspaceWindowService
    {
        public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows() => Array.Empty<IntPtr>();
        public string GetWindowTitle(IntPtr windowHandle) => string.Empty;
        public IntPtr GetForegroundWindow() => IntPtr.Zero;
        public void SetVisible(IntPtr windowHandle, bool visible) { }
        public WorkspaceFocusResult Focus(IntPtr windowHandle) => WorkspaceFocusResult.Success();
    }
}
