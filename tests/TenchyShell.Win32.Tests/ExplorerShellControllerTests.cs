using TenchyShell.Win32;
using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class ExplorerShellControllerTests
{
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void WorkspaceTrackingAcceptsAnAltTabRepresentativeWithAnOwner()
    {
        var window = CreateWorkspaceWindow(owner: (IntPtr)20, rootOwner: (IntPtr)20, altTabRepresentative: (IntPtr)10);

        var decision = WorkspaceWindowService.DecideWindowTracking(window, currentProcessId: 1);

        Assert.True(decision.Include);
        Assert.Equal("alt_tab_representative", decision.Reason);
    }

    [Fact]
    public void WorkspaceTrackingExcludesAnOwnedPopupWhenAnotherWindowRepresentsItInAltTab()
    {
        var window = CreateWorkspaceWindow(owner: (IntPtr)20, rootOwner: (IntPtr)20, altTabRepresentative: (IntPtr)20);

        var decision = WorkspaceWindowService.DecideWindowTracking(window, currentProcessId: 1);

        Assert.False(decision.Include);
        Assert.Equal("owned_or_popup_window", decision.Reason);
    }

    [Fact]
    public void WorkspaceTrackingExcludesToolWindowsBeforeApplyingAltTabRules()
    {
        var window = CreateWorkspaceWindow(extendedStyle: NativeMethods.WS_EX_TOOLWINDOW);

        var decision = WorkspaceWindowService.DecideWindowTracking(window, currentProcessId: 1);

        Assert.False(decision.Include);
        Assert.Equal("tool_window", decision.Reason);
    }

    [Fact]
    public void TryExitCurrentSessionRejectsAStableShellExitWithResidualExplorerProcess()
    {
        var platform = new FakePlatform([42]);
        platform.SetTrayWindows(new IntPtr(1234), IntPtr.Zero, IntPtr.Zero);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(2),
            Millisecond);

        Assert.False(result.Succeeded);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal(ExplorerShellState.ResidualProcess, result.State);
        Assert.Contains("proceso residual", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, platform.PostCount);
    }

    private static WorkspaceWindowSnapshot CreateWorkspaceWindow(
        IntPtr? owner = null,
        IntPtr? rootOwner = null,
        IntPtr? altTabRepresentative = null,
        long extendedStyle = 0) => new(
            Handle: (IntPtr)10,
            IsVisible: true,
            ProcessId: 2,
            Title: "Administrador de tareas",
            ClassName: "TaskManagerWindow",
            Owner: owner ?? IntPtr.Zero,
            RootOwner: rootOwner ?? (IntPtr)10,
            AltTabRepresentative: altTabRepresentative ?? (IntPtr)10,
            ExtendedStyle: extendedStyle,
            IsCloaked: false);

    [Fact]
    public void TryExitCurrentSessionRejectsRelaunchedExplorer()
    {
        var platform = new FakePlatform([42], [99]);
        platform.SetTrayWindows(new IntPtr(1234), IntPtr.Zero, new IntPtr(5678));
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(2),
            Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("relanzó", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TryExitCurrentSessionRequiresExactlyOneInitialExplorer(int count)
    {
        var ids = Enumerable.Range(40, count).ToArray();
        var platform = new FakePlatform(ids);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("exactamente un explorer.exe", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.PostCount);
    }

    [Fact]
    public void TryExitCurrentSessionAcceptsAnAlreadyStoppedExplorer()
    {
        var platform = new FakePlatform([]) { TrayWindow = IntPtr.Zero };
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.True(result.Succeeded);
        Assert.Null(result.ProcessId);
        Assert.Equal(ExplorerShellState.Stopped, result.State);
        Assert.Contains("ya estaba ausente", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.PostCount);
    }

    [Fact]
    public void CurrentSessionStateDoesNotAllowRecoveryWhenExplorerIsResidual()
    {
        var platform = new FakePlatform([42]) { TrayWindow = IntPtr.Zero };
        var controller = new ExplorerShellController(platform);

        var result = controller.GetCurrentSessionState();

        Assert.False(result.Succeeded);
        Assert.Equal(ExplorerShellState.ResidualProcess, result.State);
        Assert.Equal(42, result.ProcessId);
    }

    [Fact]
    public void TryExitCurrentSessionRejectsTrayFromAnotherProcess()
    {
        var platform = new FakePlatform([42]) { TrayProcessId = 99 };
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("PID 99", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.PostCount);
    }

    [Fact]
    public void TryExitCurrentSessionReportsRejectedExitMessage()
    {
        var platform = new FakePlatform([42]) { PostSucceeds = false, LastError = 5 };
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("error Win32 5", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, platform.PostCount);
    }

    [Fact]
    public void TryExitCurrentSessionRejectsAmbiguousStateAfterRequest()
    {
        var platform = new FakePlatform([42], [42, 99]);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(2),
            Millisecond,
            Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguo", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExitCurrentSessionTimesOutWhenOriginalProcessRemains()
    {
        var platform = new FakePlatform([42]);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(2),
            Millisecond,
            Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("Shell_TrayWnd", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, platform.DelayCount);
    }

    private sealed class FakePlatform(params int[][] processSamples) : IExplorerShellPlatform
    {
        private readonly Queue<int[]> samples = new(processSamples);
        private int[] lastSample = [];

        public int CurrentSessionId => 8;
        public int LastError { get; set; }
        public IntPtr TrayWindow { get; set; } = new(1234);
        public int TrayProcessId { get; set; } = 42;
        public bool PostSucceeds { get; set; } = true;
        public int PostCount { get; private set; }
        public int DelayCount { get; private set; }

        public void SetTrayWindows(params IntPtr[] samples)
        {
            foreach (var sample in samples) traySamples.Enqueue(sample);
        }

        public IReadOnlyList<int> GetExplorerProcessIds(int sessionId)
        {
            Assert.Equal(8, sessionId);
            if (samples.Count > 0) lastSample = samples.Dequeue();
            return lastSample;
        }

        private readonly Queue<IntPtr> traySamples = new();

        public IntPtr FindShellTrayWindow()
        {
            if (traySamples.Count > 0) TrayWindow = traySamples.Dequeue();
            return TrayWindow;
        }

        public int GetWindowProcessId(IntPtr window)
        {
            Assert.Equal(TrayWindow, window);
            return TrayProcessId;
        }

        public bool PostExplorerExit(IntPtr window)
        {
            Assert.Equal(TrayWindow, window);
            PostCount++;
            return PostSucceeds;
        }

        public void Delay(TimeSpan duration)
        {
            Assert.Equal(Millisecond, duration);
            DelayCount++;
        }
    }
}
