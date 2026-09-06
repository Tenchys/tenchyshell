using TenchyShell.Core.Windows;
using TenchyShell.Workspaces;
using Xunit;

namespace TenchyShell.Workspaces.Tests;

public sealed class MinimizedWindowServiceTests
{
    [Fact]
    public void ListsOnlyMinimizedOperableWindowsFromTheActiveWorkspace()
    {
        var source = new FakeWorkspaceSource
        {
            CurrentWorkspace = 2,
            Windows = new[] { (IntPtr)10, (IntPtr)20, (IntPtr)30, (IntPtr)40, (IntPtr)50, (IntPtr)60, (IntPtr)70 }
        };
        var windows = new FakeWorkspaceWindowService();
        windows.Metadata[(IntPtr)10] = Metadata("Zeta", workspaceWindow: true, visible: true, minimized: true);
        windows.Metadata[(IntPtr)20] = Metadata("Alfa", workspaceWindow: true, visible: true, minimized: true);
        windows.Metadata[(IntPtr)30] = Metadata("Otro workspace", workspaceWindow: true, visible: true, minimized: true);
        windows.Metadata[(IntPtr)40] = Metadata("Restaurada", workspaceWindow: true, visible: true, minimized: false);
        windows.Metadata[(IntPtr)50] = Metadata("Oculta", workspaceWindow: true, visible: false, minimized: true);
        windows.Metadata[(IntPtr)60] = Metadata("Auxiliar", workspaceWindow: false, visible: true, minimized: true);
        windows.Metadata[(IntPtr)70] = Metadata("", workspaceWindow: true, visible: true, minimized: true);
        source.Windows = source.Windows.Where(handle => handle != (IntPtr)30).ToArray();

        var service = new MinimizedWindowService(source, windows);
        var state = service.GetCurrentState();

        Assert.Equal(new[] { (IntPtr)20, (IntPtr)10 }, state.Items.Select(item => item.Handle));
        Assert.All(state.Items, item => Assert.Equal(2, item.Workspace));
        Assert.Equal(1, source.RefreshCount);
    }

    [Fact]
    public void StateOrdersByTitleAndHandleAndSupportsNavigationAndRemoval()
    {
        var state = new MinimizedWindowMenuState(new[]
        {
            new MinimizedWindowEntry((IntPtr)30, "Beta", 1),
            new MinimizedWindowEntry((IntPtr)20, "Alfa", 1),
            new MinimizedWindowEntry((IntPtr)10, "Alfa", 1),
            new MinimizedWindowEntry(IntPtr.Zero, "Inválida", 1),
            new MinimizedWindowEntry((IntPtr)40, "Otro workspace", 2),
            new MinimizedWindowEntry((IntPtr)50, " ", 1),
            new MinimizedWindowEntry((IntPtr)30, "Duplicada", 1)
        }, activeWorkspace: 1);

        Assert.Equal(new[] { (IntPtr)10, (IntPtr)20, (IntPtr)30 }, state.Items.Select(item => item.Handle));
        Assert.Equal((IntPtr)10, state.SelectedItem?.Handle);

        state.Move();
        Assert.Equal((IntPtr)20, state.SelectedItem?.Handle);
        state.Move(backwards: true);
        Assert.Equal((IntPtr)10, state.SelectedItem?.Handle);
        Assert.True(state.TrySelect(2));
        Assert.Equal((IntPtr)30, state.SelectedItem?.Handle);
        Assert.False(state.TrySelect(99));
        Assert.True(state.Remove((IntPtr)30));
        Assert.Equal((IntPtr)20, state.SelectedItem?.Handle);
        Assert.False(state.Remove((IntPtr)999));
    }

    [Fact]
    public void RestoreAndFocusUsesTheSelectedEntryAndPropagatesFocusFailures()
    {
        var source = new FakeWorkspaceSource
        {
            CurrentWorkspace = 1,
            Windows = new[] { (IntPtr)101, (IntPtr)202 }
        };
        var windows = new FakeWorkspaceWindowService();
        windows.Metadata[(IntPtr)101] = Metadata("Notepad", workspaceWindow: true, visible: true, minimized: true);
        windows.Metadata[(IntPtr)202] = Metadata("Terminal", workspaceWindow: true, visible: true, minimized: true);
        windows.RestoreResult = WorkspaceFocusResult.Failed(WorkspaceFocusFailure.AccessDenied);
        var service = new MinimizedWindowService(source, windows);

        var first = service.GetCurrentState().Items[0];
        var result = service.RestoreAndFocus(first);

        Assert.Equal(WorkspaceFocusFailure.AccessDenied, result.Failure);
        Assert.Equal(first.Handle, windows.RestoreRequested);
        Assert.True(source.RefreshCount >= 2);

        source.Windows = Array.Empty<IntPtr>();
        result = service.RestoreAndFocus(first);

        Assert.Equal(WorkspaceFocusFailure.InvalidWindow, result.Failure);
    }

    [Fact]
    public void EmptyStateIsExplicitAndInvalidEntryCannotBeRestored()
    {
        var source = new FakeWorkspaceSource { CurrentWorkspace = 3 };
        var service = new MinimizedWindowService(source, new FakeWorkspaceWindowService());

        var state = service.GetCurrentState();

        Assert.True(state.IsEmpty);
        Assert.Null(state.SelectedItem);
        var result = service.RestoreAndFocus(new MinimizedWindowEntry((IntPtr)55, "Ventana", 3));
        Assert.Equal(WorkspaceFocusFailure.InvalidWindow, result.Failure);
    }

    [Fact]
    public void RestoreAndFocusCanReportWindowsRejectedWithoutRemovingTheEntry()
    {
        var source = new FakeWorkspaceSource
        {
            CurrentWorkspace = 1,
            Windows = new[] { (IntPtr)77 }
        };
        var windows = new FakeWorkspaceWindowService
        {
            RestoreResult = WorkspaceFocusResult.Failed(WorkspaceFocusFailure.WindowsRejected)
        };
        windows.Metadata[(IntPtr)77] = Metadata("Aplicación", workspaceWindow: true, visible: true, minimized: true);
        var service = new MinimizedWindowService(source, windows);
        var state = service.GetCurrentState();

        var result = service.RestoreAndFocus(state.SelectedItem!);

        Assert.Equal(WorkspaceFocusFailure.WindowsRejected, result.Failure);
        Assert.Single(service.GetCurrentState().Items);
    }

    [Fact]
    public void KeepsAWindowHiddenByTenchyShellInTheMenuAndMakesItVisibleBeforeRestore()
    {
        var source = new FakeWorkspaceSource
        {
            CurrentWorkspace = 1,
            Windows = new[] { (IntPtr)88 }
        };
        var windows = new FakeWorkspaceWindowService();
        windows.Metadata[(IntPtr)88] = Metadata("Terminal", workspaceWindow: true, visible: false, minimized: true) with
        {
            IsOperable = false
        };
        var visibility = new FakeMinimizedWindowVisibilityService((IntPtr)88);
        var service = new MinimizedWindowService(source, windows, visibility);

        var state = service.GetCurrentState();
        var result = service.RestoreAndFocus(state.SelectedItem!);

        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.False(visibility.IsManagedHidden((IntPtr)88));
        Assert.Contains((IntPtr)88, windows.VisibleWindows);
        Assert.Equal((IntPtr)88, windows.RestoreRequested);
    }

    private static WorkspaceWindowMetadata Metadata(string title, bool workspaceWindow, bool visible, bool minimized) =>
        new(
            Exists: workspaceWindow,
            IsVisible: visible,
            IsMinimized: minimized,
            Title: title,
            IsOperable: workspaceWindow);

    private sealed class FakeWorkspaceSource : IWorkspaceWindowSource
    {
        public int CurrentWorkspace { get; set; } = 1;

        public IReadOnlyList<IntPtr> Windows { get; set; } = Array.Empty<IntPtr>();

        public int RefreshCount { get; private set; }

        public IReadOnlyList<IntPtr> GetCurrentWorkspaceWindows() => Windows;

        public void Refresh() => RefreshCount++;
    }

    private sealed class FakeWorkspaceWindowService : IWorkspaceWindowService
    {
        public Dictionary<IntPtr, WorkspaceWindowMetadata> Metadata { get; } = new();

        public WorkspaceFocusResult RestoreResult { get; set; } = WorkspaceFocusResult.Success();

        public IntPtr RestoreRequested { get; private set; }

        public List<IntPtr> VisibleWindows { get; } = new();

        public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows() => Metadata.Keys.ToArray();

        public string GetWindowTitle(IntPtr windowHandle) => Metadata.TryGetValue(windowHandle, out var metadata) ? metadata.Title : string.Empty;

        public IntPtr GetForegroundWindow() => IntPtr.Zero;

        public void SetVisible(IntPtr windowHandle, bool visible)
        {
            if (visible) VisibleWindows.Add(windowHandle);
            if (Metadata.TryGetValue(windowHandle, out var metadata))
            {
                Metadata[windowHandle] = metadata with
                {
                    IsVisible = visible,
                    IsOperable = visible && metadata.Exists
                };
            }
        }

        public WorkspaceFocusResult Focus(IntPtr windowHandle) => WorkspaceFocusResult.Success();

        public WorkspaceWindowMetadata GetWindowMetadata(IntPtr windowHandle) =>
            Metadata.TryGetValue(windowHandle, out var metadata)
                ? metadata
                : new WorkspaceWindowMetadata(false, false, false, string.Empty, false);

        public WorkspaceFocusResult RestoreAndFocus(IntPtr windowHandle)
        {
            RestoreRequested = windowHandle;
            return RestoreResult;
        }
    }

    private sealed class FakeMinimizedWindowVisibilityService(IntPtr managedWindow) : IMinimizedWindowVisibilityService
    {
        private bool managedHidden = true;

        public bool IsManagedHidden(IntPtr windowHandle) => managedHidden && windowHandle == managedWindow;

        public void Start() { }

        public void Synchronize() { }

        public void Forget(IntPtr windowHandle)
        {
            if (windowHandle == managedWindow) managedHidden = false;
        }

        public void Dispose() { }
    }
}
