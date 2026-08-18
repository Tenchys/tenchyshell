using Xunit;
using MinimalShell.Core.Windows;
using MinimalShell.Win32;
using MinimalShell.Workspaces;

namespace MinimalShell.Workspaces.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void TestProjectIsConfigured()
    {
        Assert.True(true);
    }

    [Fact]
    public void HotkeyParserSupportsLauncherCombination()
    {
        var parsed = HotkeyParser.TryParse("Ctrl+Alt+Space", out var combination, out var error);

        Assert.True(parsed, error);
        Assert.NotEqual(0u, combination.Modifiers);
        Assert.Equal(0x20u, combination.VirtualKey);
    }

    [Fact]
    public void HotkeyParserRejectsMissingModifier()
    {
        var parsed = HotkeyParser.TryParse("Space", out _, out var error);

        Assert.False(parsed);
        Assert.Contains("modificadores", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HotkeyParserSupportsFunctionKeyWithoutModifier()
    {
        var parsed = HotkeyParser.TryParse("F12", out var combination, out var error);

        Assert.True(parsed, error);
        Assert.Equal(0x7Bu, combination.VirtualKey);
    }

    [Fact]
    public void HotkeyParserSupportsModifiedFunctionKeys()
    {
        var parsed = HotkeyParser.TryParse("Ctrl+F12", out var combination, out var error);

        Assert.True(parsed, error);
        Assert.Equal(0x7Bu, combination.VirtualKey);
    }

    [Fact]
    public void HotkeyParserSupportsArrowKeys()
    {
        var parsed = HotkeyParser.TryParse("Ctrl+Alt+Left", out var combination, out var error);

        Assert.True(parsed, error);
        Assert.Equal(0x25u, combination.VirtualKey);
    }

    [Fact]
    public void WindowServiceRejectsAnInvalidForegroundWindow()
    {
        var service = new WindowService(new FakeWindowNativeApi { ForegroundWindow = IntPtr.Zero }, currentProcessId: 10);

        var result = service.CloseActiveWindow();

        Assert.False(result.Succeeded);
        Assert.Contains("válida", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowServiceDoesNotCloseMinimalShellWindows()
    {
        var nativeApi = new FakeWindowNativeApi { ForegroundWindow = (IntPtr)123, ProcessId = 10 };
        var service = new WindowService(nativeApi, currentProcessId: 10);

        var result = service.CloseActiveWindow();

        Assert.False(result.Succeeded);
        Assert.False(nativeApi.CloseRequested);
    }

    [Fact]
    public void WindowServicePostsCloseMessageToAnotherProcess()
    {
        var nativeApi = new FakeWindowNativeApi { ForegroundWindow = (IntPtr)123, ProcessId = 20 };
        var service = new WindowService(nativeApi, currentProcessId: 10);

        var result = service.CloseActiveWindow();

        Assert.True(result.Succeeded, result.Error);
        Assert.True(nativeApi.CloseRequested);
    }

    [Fact]
    public void WorkspaceManagerSwitchesVisibilityAndFocus()
    {
        var windowService = new FakeWorkspaceWindowService
        {
            VisibleWindows = new[] { (IntPtr)101, (IntPtr)202 }
        };
        var manager = new WorkspaceManager(windowService);

        var result = manager.SwitchTo(2);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains((IntPtr)101, windowService.HiddenWindows);
        Assert.Contains((IntPtr)202, windowService.HiddenWindows);
        Assert.Equal(2, manager.CurrentWorkspace);

        Assert.True(manager.SwitchTo(1).Succeeded);
        Assert.Contains((IntPtr)101, windowService.ShownWindows);
        Assert.Contains((IntPtr)202, windowService.ShownWindows);
        Assert.Equal((IntPtr)101, windowService.LastFocusedWindow);
    }

    [Fact]
    public void WorkspaceManagerMovesForegroundWindowToAnotherWorkspace()
    {
        var windowService = new FakeWorkspaceWindowService
        {
            VisibleWindows = new[] { (IntPtr)101 },
            ForegroundWindow = (IntPtr)101
        };
        var manager = new WorkspaceManager(windowService);

        var result = manager.MoveForegroundTo(3);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains((IntPtr)101, windowService.HiddenWindows);
        Assert.True(manager.SwitchTo(3).Succeeded);
        Assert.Contains((IntPtr)101, windowService.ShownWindows);
    }

    [Fact]
    public void WindowServiceMovesActiveWindowAndKeepsItInsideWorkArea()
    {
        var nativeApi = new FakeWindowNativeApi
        {
            ForegroundWindow = (IntPtr)123,
            ProcessId = 20,
            CurrentRect = new WindowRect(100, 100, 500, 400),
            WorkArea = new WindowRect(0, 0, 600, 500)
        };
        var service = new WindowService(nativeApi, currentProcessId: 10);

        var result = service.MoveActiveWindow(1000, 1000);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(new WindowRect(200, 200, 600, 500), nativeApi.LastPosition);
    }

    [Fact]
    public void WindowServiceMaximizesAndRestoresActiveWindow()
    {
        var nativeApi = new FakeWindowNativeApi { ForegroundWindow = (IntPtr)123, ProcessId = 20 };
        var service = new WindowService(nativeApi, currentProcessId: 10);

        Assert.True(service.MaximizeActiveWindow().Succeeded);
        Assert.True(service.RestoreActiveWindow().Succeeded);
        Assert.True(nativeApi.FocusRequested is false);
        Assert.NotNull(nativeApi.LastShowCommand);
    }

    private sealed class FakeWorkspaceWindowService : IWorkspaceWindowService
    {
        public IReadOnlyList<IntPtr> VisibleWindows { get; set; } = Array.Empty<IntPtr>();

        public IntPtr ForegroundWindow { get; set; }

        public List<IntPtr> HiddenWindows { get; } = new();

        public List<IntPtr> ShownWindows { get; } = new();

        public IntPtr LastFocusedWindow { get; private set; }

        public IReadOnlyList<IntPtr> GetVisibleTopLevelWindows() => VisibleWindows;

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public void SetVisible(IntPtr windowHandle, bool visible)
        {
            (visible ? ShownWindows : HiddenWindows).Add(windowHandle);
        }

        public bool Focus(IntPtr windowHandle)
        {
            LastFocusedWindow = windowHandle;
            return true;
        }
    }

    private sealed class FakeWindowNativeApi : IWindowNativeApi
    {
        public IntPtr ForegroundWindow { get; init; }

        public uint ProcessId { get; init; }

        public bool CloseRequested { get; private set; }

        public WindowRect CurrentRect { get; set; } = new(100, 100, 500, 400);

        public WindowRect WorkArea { get; set; } = new(0, 0, 1920, 1080);

        public WindowRect? LastPosition { get; private set; }

        public uint? LastShowCommand { get; private set; }

        public bool FocusRequested { get; private set; }

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public bool IsWindow(IntPtr windowHandle) => windowHandle != IntPtr.Zero;

        public uint GetWindowProcessId(IntPtr windowHandle) => ProcessId;

        public bool PostCloseMessage(IntPtr windowHandle, out int errorCode)
        {
            CloseRequested = true;
            errorCode = 0;
            return true;
        }

        public bool TryGetWindowRect(IntPtr windowHandle, out WindowRect windowRect)
        {
            windowRect = CurrentRect;
            return true;
        }

        public bool TryGetWorkArea(IntPtr windowHandle, out WindowRect workArea)
        {
            workArea = WorkArea;
            return true;
        }

        public bool SetWindowPosition(IntPtr windowHandle, WindowRect windowRect)
        {
            LastPosition = windowRect;
            CurrentRect = windowRect;
            return true;
        }

        public bool ShowWindow(IntPtr windowHandle, uint command)
        {
            LastShowCommand = command;
            return true;
        }

        public bool FocusWindow(IntPtr windowHandle)
        {
            FocusRequested = true;
            return true;
        }
    }
}
