using System.ComponentModel;
using System.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.App;

public sealed class ProcessLauncher : IProcessLauncher
{
    private readonly ILogger logger;

    public ProcessLauncher(ILogger logger)
    {
        this.logger = logger;
    }

    public ProcessLaunchResult Launch(ProcessLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Failure("El nombre del ejecutable no puede estar vacío.");
        }

        if (Path.IsPathFullyQualified(request.FileName) && !File.Exists(request.FileName))
        {
            return Failure($"No existe el ejecutable '{request.FileName}'.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            {
                startInfo.WorkingDirectory = request.WorkingDirectory;
            }

            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);

            if (process is null)
            {
                logger.Info($"Windows inició '{request.FileName}' sin devolver un proceso; el PID es desconocido.");
                return ProcessLaunchResult.Success(null);
            }

            return ProcessLaunchResult.Success(process.Id);
        }
        catch (Win32Exception exception)
        {
            return Failure($"No se pudo iniciar '{request.FileName}': {exception.Message}");
        }
        catch (Exception exception)
        {
            logger.Error($"Error inesperado al iniciar '{request.FileName}'.", exception);
            return Failure($"Error inesperado al iniciar '{request.FileName}': {exception.Message}");
        }
    }

    private ProcessLaunchResult Failure(string error)
    {
        logger.Error(error);
        return ProcessLaunchResult.Failure(error);
    }
}
