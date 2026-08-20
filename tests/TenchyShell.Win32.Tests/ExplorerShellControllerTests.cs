using TenchyShell.Win32;
using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class ExplorerShellControllerTests
{
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void TryExitCurrentSessionAcceptsAStableCooperativeExit()
    {
        var platform = new FakePlatform([42], [42], [], []);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(2),
            Millisecond);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal(1, platform.PostCount);
    }

    [Fact]
    public void TryExitCurrentSessionRejectsRelaunchedExplorer()
    {
        var platform = new FakePlatform([42], [], [99]);
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
    public void TryExitCurrentSessionRejectsMissingTrayWindow()
    {
        var platform = new FakePlatform([42]) { TrayWindow = IntPtr.Zero };
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.False(result.Succeeded);
        Assert.Contains("Shell_TrayWnd", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.PostCount);
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
        Assert.Contains("no terminó", result.Message, StringComparison.Ordinal);
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

        public IReadOnlyList<int> GetExplorerProcessIds(int sessionId)
        {
            Assert.Equal(8, sessionId);
            if (samples.Count > 0) lastSample = samples.Dequeue();
            return lastSample;
        }

        public IntPtr FindShellTrayWindow() => TrayWindow;

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
