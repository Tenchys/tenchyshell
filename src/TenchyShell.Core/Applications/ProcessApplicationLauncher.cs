using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.Core.Applications;

/// <summary>Coordina lanzamientos Win32 y activaciones empaquetadas.</summary>
public sealed class ProcessApplicationLauncher : IApplicationLauncher
{
    private readonly IProcessLauncher processLauncher;
    private readonly IPackagedApplicationActivator packagedApplicationActivator;
    private readonly ILogger logger;
    private readonly SessionStabilityMetrics? stabilityMetrics;

    public ProcessApplicationLauncher(
        IProcessLauncher processLauncher,
        IPackagedApplicationActivator packagedApplicationActivator,
        ILogger logger,
        SessionStabilityMetrics? stabilityMetrics = null)
    {
        this.processLauncher = processLauncher;
        this.packagedApplicationActivator = packagedApplicationActivator;
        this.logger = logger;
        this.stabilityMetrics = stabilityMetrics;
    }

    public event EventHandler<PackagedApplicationActivationFailedEventArgs>? PackagedApplicationActivationFailed;

    public ProcessLaunchResult Launch(ApplicationEntry application)
    {
        var isPackagedApplication = application.ActivationKind == ApplicationActivationKind.ShellApplication &&
                                    !string.IsNullOrWhiteSpace(application.AppUserModelId);
        PackagedApplicationActivationResult? packagedResult = null;
        var result = isPackagedApplication
            ? (packagedResult = packagedApplicationActivator.Activate(application)).LaunchResult
            : processLauncher.Launch(new ProcessLaunchRequest(application.Target, application.Arguments));
        if (isPackagedApplication)
        {
            stabilityMetrics?.RecordPackagedActivation(result);
            if (!result.Succeeded && packagedResult is not null)
            {
                PackagedApplicationActivationFailed?.Invoke(
                    this,
                    new PackagedApplicationActivationFailedEventArgs(application, packagedResult));
            }
        }

        if (result.Succeeded)
        {
            logger.Info($"Se lanzó la aplicación '{application.DisplayName}'.");
        }
        else
        {
            logger.Error($"No se pudo lanzar la aplicación '{application.DisplayName}': {result.Error}");
        }

        return result;
    }
}

public sealed class PackagedApplicationActivationFailedEventArgs : EventArgs
{
    public PackagedApplicationActivationFailedEventArgs(
        ApplicationEntry application,
        PackagedApplicationActivationResult result)
    {
        Application = application;
        Result = result;
    }

    public ApplicationEntry Application { get; }

    public PackagedApplicationActivationResult Result { get; }
}
