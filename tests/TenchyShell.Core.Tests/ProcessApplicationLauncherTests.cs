using TenchyShell.Core.Applications;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Notifications;
using TenchyShell.Core.Processes;
using Xunit;

namespace TenchyShell.Core.Tests;

public sealed class ProcessApplicationLauncherTests
{
    [Fact]
    public void NotificationCenterAddsCriticalCardsWithoutAnExternalBridge()
    {
        var center = new NotificationCenter();
        NotificationCenterChangedEventArgs? change = null;
        center.Changed += (_, args) => change = args;

        center.Add(new ShellNotification(
            "packaged-activation-1",
            "TenchyShell.PackagedActivation",
            "TenchyShell",
            "No se pudo abrir Calculadora",
            "Windows devolvió 0x80004005.",
            DateTimeOffset.UtcNow));

        Assert.Single(center.GetActive());
        Assert.NotNull(change);
        Assert.True(change!.ShowPopup);
    }

    [Fact]
    public void LaunchRaisesActivationFailureOnlyForFailedPackagedApplication()
    {
        var packagedResult = PackagedApplicationActivationResult.Failure(
            "La activación empaquetada no respondió dentro de cinco segundos.",
            unchecked((int)0x800705B4),
            PackagedActivationDiagnosticStage.ActivateApplication,
            "La activación COM no completó dentro de 5 s.",
            timedOut: true);
        var launcher = new ProcessApplicationLauncher(
            new FakeProcessLauncher(ProcessLaunchResult.Success(8)),
            new FakePackagedApplicationActivator(packagedResult),
            new NullLogger());
        PackagedApplicationActivationFailedEventArgs? failure = null;
        launcher.PackagedApplicationActivationFailed += (_, args) => failure = args;

        var result = launcher.Launch(CreatePackagedApplication());

        Assert.False(result.Succeeded);
        Assert.NotNull(failure);
        Assert.Equal("Calculadora", failure!.Application.DisplayName);
        Assert.True(failure.Result.TimedOut);
        Assert.Equal("0x800705B4", failure.Result.HResultHex);
    }

    [Fact]
    public void LaunchDoesNotRaisePackagedActivationFailureForWin32Launches()
    {
        var launcher = new ProcessApplicationLauncher(
            new FakeProcessLauncher(ProcessLaunchResult.Failure("Win32 falló.")),
            new FakePackagedApplicationActivator(PackagedApplicationActivationResult.Success(
                9,
                0,
                PackagedActivationDiagnosticStage.ActivateApplication,
                "No debería usarse.")),
            new NullLogger());
        var wasRaised = false;
        launcher.PackagedApplicationActivationFailed += (_, _) => wasRaised = true;

        var result = launcher.Launch(new ApplicationEntry(
            "Bloc de notas",
            ApplicationActivationKind.Executable,
            "notepad.exe"));

        Assert.False(result.Succeeded);
        Assert.False(wasRaised);
    }

    private static ApplicationEntry CreatePackagedApplication() => new(
        "Calculadora",
        ApplicationActivationKind.ShellApplication,
        "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

    private sealed class FakeProcessLauncher(ProcessLaunchResult result) : IProcessLauncher
    {
        public ProcessLaunchResult Launch(ProcessLaunchRequest request) => result;
    }

    private sealed class FakePackagedApplicationActivator(PackagedApplicationActivationResult result) : IPackagedApplicationActivator
    {
        public PackagedApplicationActivationResult Activate(ApplicationEntry application) => result;
    }

    private sealed class NullLogger : ILogger
    {
        public void Info(string message) { }

        public void Error(string message) { }

        public void Error(string message, Exception exception) { }
    }
}
