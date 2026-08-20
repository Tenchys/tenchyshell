using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.Core.Session;

public sealed class SessionActionService
{
    private readonly IProcessLauncher processLauncher;
    private readonly ILogger logger;

    public SessionActionService(IProcessLauncher processLauncher, ILogger logger)
    {
        this.processLauncher = processLauncher;
        this.logger = logger;
    }

    public ProcessLaunchResult Execute(SessionAction action, bool confirmed)
    {
        if (!confirmed)
        {
            var confirmationResult = ProcessLaunchResult.Failure("La acción de sesión requiere la confirmación explícita '--confirm'.");
            logger.Error(confirmationResult.Error!);
            return confirmationResult;
        }

        var request = action switch
        {
            SessionAction.Logout => new ProcessLaunchRequest("shutdown.exe", new[] { "/l" }),
            SessionAction.Shutdown => new ProcessLaunchRequest("shutdown.exe", new[] { "/s", "/t", "0" }),
            SessionAction.Restart => new ProcessLaunchRequest("shutdown.exe", new[] { "/r", "/t", "0" }),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Acción de sesión no compatible.")
        };

        var result = processLauncher.Launch(request);

        if (result.Succeeded)
        {
            logger.Info($"Se solicitó la acción de sesión: {action}.");
        }
        else
        {
            logger.Error($"No se pudo solicitar la acción de sesión {action}: {result.Error}");
        }

        return result;
    }
}
