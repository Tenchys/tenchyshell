using System.ComponentModel;
using System.Diagnostics;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.App;

public sealed class ProcessLauncher : IProcessLauncher
{
    private readonly ILogger logger;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;

    public ProcessLauncher(ILogger logger, LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.logger = logger;
        this.benchmarkRecorder = benchmarkRecorder;
    }

    public ProcessLaunchResult Launch(ProcessLaunchRequest request)
    {
        benchmarkRecorder?.Record("process_launch_requested", new
        {
            command = request.FileName,
            arguments = request.Arguments,
            workingDirectory = request.WorkingDirectory
        });

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            const string error = "El nombre del ejecutable no puede estar vacío.";
            benchmarkRecorder?.Record("process_launch_completed", new { command = request.FileName, arguments = request.Arguments, pid = (int?)null, succeeded = false, error });
            return Failure(error);
        }

        if (Path.IsPathFullyQualified(request.FileName) && !File.Exists(request.FileName))
        {
            var error = $"No existe el ejecutable '{request.FileName}'.";
            benchmarkRecorder?.Record("process_launch_completed", new { command = request.FileName, arguments = request.Arguments, pid = (int?)null, succeeded = false, error });
            return Failure(error);
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

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                logger.Info($"Windows inició '{request.FileName}' sin devolver un proceso; el PID es desconocido.");
                benchmarkRecorder?.Record("process_launch_completed", new
                {
                    command = request.FileName,
                    arguments = request.Arguments,
                    pid = (int?)null,
                    succeeded = true,
                    error = (string?)null
                });
                return ProcessLaunchResult.Success(null);
            }

            benchmarkRecorder?.Record("process_launch_completed", new
            {
                command = request.FileName,
                arguments = request.Arguments,
                pid = process.Id,
                succeeded = true,
                error = (string?)null
            });
            return ProcessLaunchResult.Success(process.Id);
        }
        catch (Win32Exception exception)
        {
            var error = $"No se pudo iniciar '{request.FileName}': {exception.Message}";
            benchmarkRecorder?.Record("process_launch_completed", new { command = request.FileName, arguments = request.Arguments, pid = (int?)null, succeeded = false, error });
            return Failure(error);
        }
        catch (Exception exception)
        {
            logger.Error($"Error inesperado al iniciar '{request.FileName}'.", exception);
            var error = $"Error inesperado al iniciar '{request.FileName}': {exception.Message}";
            benchmarkRecorder?.Record("process_launch_completed", new { command = request.FileName, arguments = request.Arguments, pid = (int?)null, succeeded = false, error });
            return Failure(error);
        }
    }

    private ProcessLaunchResult Failure(string error)
    {
        logger.Error(error);
        return ProcessLaunchResult.Failure(error);
    }
}
