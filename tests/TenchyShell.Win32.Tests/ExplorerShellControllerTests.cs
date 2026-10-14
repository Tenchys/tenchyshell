using TenchyShell.Win32;
using TenchyShell.Core.Applications;
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
    public void PackagedApplicationActivatorRejectsAnEntryWithoutAppUserModelId()
    {
        var application = new ApplicationEntry(
            "Calculadora",
            ApplicationActivationKind.ShellApplication,
            "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        var result = new WindowsPackagedApplicationActivator().Activate(application);

        Assert.False(result.Succeeded);
        Assert.Contains("AppUserModelId", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedApplicationActivatorRunsNormalLaunchOnDedicatedMtaInProcessContext()
    {
        var platform = new FakeApplicationActivationPlatform(
            new PackagedActivationAttempt(
                0,
                42,
                "Activación aceptada por Windows.",
                PackagedActivationDiagnosticStage.ActivateApplication));
        var activator = new WindowsPackagedApplicationActivator(platform);
        var application = new ApplicationEntry(
            "Calculadora",
            ApplicationActivationKind.ShellApplication,
            "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        var result = activator.Activate(application);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.ProcessId);
        Assert.Equal(PackagedApplicationActivationContext.InProcess, platform.DedicatedMtaContext);
        Assert.Equal(1, platform.DedicatedMtaCalls);
    }

    [Fact]
    public void PackagedActivationDiagnosticUsesDedicatedMtaAndKeepsAumidOutOfFailureMessage()
    {
        const int hresult = unchecked((int)0x80040900);
        var platform = new FakeApplicationActivationPlatform(PackagedActivationAttempt.Failure(
            hresult,
            "La activación fue rechazada.",
            PackagedActivationDiagnosticStage.CoCreateInstance));
        var activator = new WindowsPackagedApplicationActivator(platform);

        var result = activator.Diagnose("Contoso.Private_123!App", PackagedApplicationActivationContext.LocalServer, "Stopped");

        Assert.False(result.Succeeded);
        Assert.Equal(hresult, result.HResult);
        Assert.Equal("0x80040900", result.HResultHex);
        Assert.Equal(PackagedApplicationActivationContext.LocalServer, platform.DedicatedMtaContext);
        Assert.Equal(PackagedActivationDiagnosticStage.CoCreateInstance, result.Stage);
        Assert.Equal(1, platform.DedicatedMtaCalls);
        Assert.DoesNotContain("Contoso.Private_123!App", result.Message, StringComparison.Ordinal);
        Assert.True(result.FinishedAt >= result.StartedAt);
    }

    [Fact]
    public void PackagedApplicationActivatorPreservesTimeoutHResultAndComStage()
    {
        var platform = new FakeApplicationActivationPlatform(PackagedActivationAttempt.Timeout(
            "La activación COM no completó dentro de 5 s.",
            PackagedActivationDiagnosticStage.ActivateApplication));
        var activator = new WindowsPackagedApplicationActivator(platform);
        var application = new ApplicationEntry(
            "Calculadora",
            ApplicationActivationKind.ShellApplication,
            "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        var result = activator.Activate(application);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Equal("0x800705B4", result.HResultHex);
        Assert.Equal(PackagedActivationDiagnosticStage.ActivateApplication, result.Stage);
        Assert.DoesNotContain(application.AppUserModelId!, result.Error!, StringComparison.Ordinal);
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
    public void WorkspaceTrackingExcludesVisibleWindowsWithAnEmptyNativeTitle()
    {
        var window = CreateWorkspaceWindow(title: string.Empty);

        var decision = WorkspaceWindowService.DecideWindowTracking(window, currentProcessId: 1);

        Assert.False(decision.Include);
        Assert.Equal("empty_title", decision.Reason);
    }

    [Fact]
    public void TryExitCurrentSessionRetriesResidualProcessAndEventuallySucceeds()
    {
        var platform = new FakePlatform([42], [42], [42], [42], [42], [], []);
        platform.SetTrayWindows(new IntPtr(1234), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(2),
            Millisecond);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.CanContinueWithoutExplorer);
        Assert.Equal(ExplorerShellState.Stopped, result.State);
        Assert.Contains("Intentos previos", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, platform.PostCount);
        Assert.Contains(TimeSpan.FromSeconds(2), platform.Delays);
    }

    [Fact]
    public void TryExitCurrentSessionRequiresTrayAndProcessesToStayAbsentForStablePeriod()
    {
        var platform = new FakePlatform([42], [42], [42], [], []);
        platform.SetTrayWindows(new IntPtr(1234), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(2),
            Millisecond);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(ExplorerShellState.Stopped, result.State);
        Assert.Null(result.ProcessId);
        Assert.Equal(4, platform.DelayCount);
    }

    [Fact]
    public void TryExitCurrentSessionRequestsExitAgainWhenTrayReappearsBetweenAttempts()
    {
        var platform = new FakePlatform([42], [42], [42], []);
        platform.SetTrayWindows(new IntPtr(1234), IntPtr.Zero, new IntPtr(5678), IntPtr.Zero);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(ExplorerShellState.Stopped, result.State);
        Assert.Equal(2, platform.PostCount);
        Assert.Equal(1, platform.Delays.Count(duration => duration == TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void TryExitCurrentSessionFailsAfterFourAttemptsAndNeverTerminatesProcess()
    {
        var platform = new FakePlatform([42]);
        var controller = new ExplorerShellController(platform);

        var result = controller.TryExitCurrentSession(Millisecond, Millisecond, Millisecond);

        Assert.False(result.Succeeded);
        Assert.False(result.CanContinueWithoutExplorer);
        Assert.Equal(ExplorerShellState.Running, result.State);
        Assert.Contains("Se agotaron 4 intentos", result.Message, StringComparison.Ordinal);
        Assert.Equal(4, platform.PostCount);
        Assert.Equal(3, platform.Delays.Count(duration => duration == TimeSpan.FromSeconds(2)));
    }

    private static WorkspaceWindowSnapshot CreateWorkspaceWindow(
        IntPtr? owner = null,
        IntPtr? rootOwner = null,
        IntPtr? altTabRepresentative = null,
        long extendedStyle = 0,
        string title = "Administrador de tareas") => new(
            Handle: (IntPtr)10,
            IsVisible: true,
            ProcessId: 2,
            Title: title,
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
        Assert.Contains("PID 99", result.Message, StringComparison.Ordinal);
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
        Assert.True(result.CanContinueWithoutExplorer);
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
        Assert.False(result.CanContinueWithoutExplorer);
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
        Assert.Equal(4, platform.PostCount);
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
        Assert.Equal(11, platform.DelayCount);
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
        public List<TimeSpan> Delays { get; } = [];

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
            DelayCount++;
            Delays.Add(duration);
        }
    }

    private sealed class FakeApplicationActivationPlatform(PackagedActivationAttempt result) : IApplicationActivationPlatform
    {
        public PackagedApplicationActivationContext? DedicatedMtaContext { get; private set; }
        public int DedicatedMtaCalls { get; private set; }

        public PackagedActivationAttempt ActivateApplicationOnDedicatedMta(string appUserModelId, string arguments, PackagedApplicationActivationContext context)
        {
            DedicatedMtaContext = context;
            DedicatedMtaCalls++;
            return result;
        }
    }
}
