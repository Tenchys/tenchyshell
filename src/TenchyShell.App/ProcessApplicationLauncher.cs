using TenchyShell.Core.Applications;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.App;

public sealed class ProcessApplicationLauncher : IApplicationLauncher
{
    private readonly IProcessLauncher processLauncher;
    private readonly ILogger logger;

    public ProcessApplicationLauncher(IProcessLauncher processLauncher, ILogger logger)
    {
        this.processLauncher = processLauncher;
        this.logger = logger;
    }

    public ProcessLaunchResult Launch(ApplicationEntry application)
    {
        var result = processLauncher.Launch(new ProcessLaunchRequest(
            application.Target,
            application.Arguments));

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
