using TenchyShell.Core.Windows;
using Xunit;

namespace TenchyShell.Workspaces.Tests;

public sealed class WindowSnapshotOperationTests
{
    [Fact]
    public void WorkspaceCaptureUsesOneEnumerationAndDoesNotQueryMetadataAgain()
    {
        var service = new SnapshotWindowService(new WorkspaceWindowSnapshot(
            (IntPtr)101,
            new WorkspaceWindowMetadata(true, true, false, "Terminal", true), 42));
        var manager = new WorkspaceManager(service);

        var snapshot = manager.CaptureCurrentWorkspaceSnapshot("test");

        Assert.Equal(1, service.CaptureCalls);
        Assert.Equal(0, service.MetadataCalls);
        Assert.Equal(1, snapshot.Capture?.EnumWindowsCalls);
        Assert.Equal(1, snapshot.Capture?.MetadataQueries);
        Assert.Equal((IntPtr)101, Assert.Single(snapshot.CurrentWindows).Handle);
    }

    [Fact]
    public void WorkspaceSkipsDestroyedOrReusedHandleBeforeHiding()
    {
        var service = new SnapshotWindowService(new WorkspaceWindowSnapshot(
            (IntPtr)101,
            new WorkspaceWindowMetadata(true, true, false, "Old window", true), 42))
        {
            RevalidateResult = false
        };
        var manager = new WorkspaceManager(service);

        Assert.True(manager.SwitchTo(2).Succeeded);
        Assert.Empty(service.VisibilityChanges);
        Assert.Equal(1, service.RevalidationCalls);
    }

    [Fact]
    public void MinimizedMenuUsesCapturedMetadataAndOmitsUntitledOrSpecialWindows()
    {
        var source = new SnapshotSource(1, new[]
        {
            new WorkspaceWindowSnapshot((IntPtr)10, new WorkspaceWindowMetadata(true, true, true, "Terminal", true)),
            new WorkspaceWindowSnapshot((IntPtr)20, new WorkspaceWindowMetadata(true, true, true, "", true)),
            new WorkspaceWindowSnapshot((IntPtr)30, new WorkspaceWindowMetadata(true, true, true, "Tool window", false))
        });
        var service = new SnapshotWindowService(source.Windows.ToArray());
        var minimized = new MinimizedWindowService(source, service);

        var state = minimized.GetCurrentState();

        Assert.Equal(new[] { (IntPtr)10 }, state.Items.Select(item => item.Handle));
        Assert.Equal(0, service.MetadataCalls);
        Assert.Equal(1, source.CaptureCalls);
    }

    private sealed class SnapshotWindowService(params WorkspaceWindowSnapshot[] windows) : IWorkspaceWindowService
    {
        private readonly IReadOnlyList<WorkspaceWindowSnapshot> windows = windows;
        public int CaptureCalls { get; private set; }
        public int MetadataCalls { get; private set; }
        public int RevalidationCalls { get; private set; }
        public bool RevalidateResult { get; init; } = true;
        public List<(IntPtr Handle, bool Visible)> VisibilityChanges { get; } = new();

        public WorkspaceWindowCapture CaptureSnapshot(string operation = "workspace")
        {
            CaptureCalls++;
            return new WorkspaceWindowCapture(operation, windows, 1, windows.Count, 0, 1);
        }

        public bool RevalidateWindow(WorkspaceWindowSnapshot snapshot)
        {
            RevalidationCalls++;
            return RevalidateResult;
        }

        public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows() => windows.Select(item => item.Handle).ToArray();
        public string GetWindowTitle(IntPtr windowHandle) => throw new InvalidOperationException("metadata duplicada");
        public bool WindowExists(IntPtr windowHandle) => windows.Any(item => item.Handle == windowHandle);
        public IntPtr GetForegroundWindow() => IntPtr.Zero;
        public void SetVisible(IntPtr windowHandle, bool visible) => VisibilityChanges.Add((windowHandle, visible));
        public WorkspaceFocusResult Focus(IntPtr windowHandle) => WorkspaceFocusResult.Success();
        public WorkspaceWindowMetadata GetWindowMetadata(IntPtr windowHandle)
        {
            MetadataCalls++;
            throw new InvalidOperationException("metadata duplicada");
        }
    }

    private sealed class SnapshotSource(int workspace, IReadOnlyList<WorkspaceWindowSnapshot> windows) : IWorkspaceWindowSource
    {
        public int CurrentWorkspace => workspace;
        public IReadOnlyList<WorkspaceWindowSnapshot> Windows { get; } = windows;
        public int CaptureCalls { get; private set; }
        public IReadOnlyList<IntPtr> GetCurrentWorkspaceWindows() => Windows.Select(item => item.Handle).ToArray();
        public WorkspaceOperationSnapshot CaptureCurrentWorkspaceSnapshot(string operation = "workspace")
        {
            CaptureCalls++;
            var capture = new WorkspaceWindowCapture(operation, Windows, 1, Windows.Count, 0, 1);
            return new WorkspaceOperationSnapshot(
                operation,
                workspace,
                new Dictionary<int, IReadOnlyList<WorkspaceWindowSnapshot>> { [workspace] = Windows },
                capture);
        }
        public void Refresh() { }
    }
}
