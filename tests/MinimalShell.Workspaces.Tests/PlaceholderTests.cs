using Xunit;
using MinimalShell.Win32;

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

    private sealed class FakeWindowNativeApi : IWindowNativeApi
    {
        public IntPtr ForegroundWindow { get; init; }

        public uint ProcessId { get; init; }

        public bool CloseRequested { get; private set; }

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public bool IsWindow(IntPtr windowHandle) => windowHandle != IntPtr.Zero;

        public uint GetWindowProcessId(IntPtr windowHandle) => ProcessId;

        public bool PostCloseMessage(IntPtr windowHandle, out int errorCode)
        {
            CloseRequested = true;
            errorCode = 0;
            return true;
        }
    }
}
