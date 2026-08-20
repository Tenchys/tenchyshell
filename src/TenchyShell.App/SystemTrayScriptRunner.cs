using System.Diagnostics;
using System.Text.Json;
using TenchyShell.Core.SystemTray;

namespace TenchyShell.App;

public sealed class SystemTrayScriptRunner : ISystemTrayScriptRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SystemTrayScriptOutputResult> RunAsync(
        SystemTrayItemConfiguration item,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Command))
        {
            return SystemTrayScriptOutputResult.Failure("El elemento no tiene command configurado.");
        }

        var command = ResolvePath(item.Command, workingDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in item.Arguments)
        {
            process.StartInfo.ArgumentList.Add(ResolvePath(argument, workingDirectory));
        }

        try
        {
            if (!process.Start())
            {
                return SystemTrayScriptOutputResult.Failure($"No se pudo iniciar '{item.Command}'.");
            }

            using (process)
            using (cancellationToken.Register(() => TryKill(process)))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return SystemTrayScriptOutputResult.Failure(
                        string.IsNullOrWhiteSpace(error)
                            ? $"El script terminó con código {process.ExitCode}."
                            : error.Trim());
                }

                if (output.Length > 32768)
                {
                    return SystemTrayScriptOutputResult.Failure("La salida JSON del script supera 32 KiB.");
                }

                try
                {
                    var result = JsonSerializer.Deserialize<SystemTrayScriptOutput>(output, JsonOptions);
                    return result is null
                        ? SystemTrayScriptOutputResult.Failure("El script devolvió JSON vacío.")
                        : SystemTrayScriptOutputResult.Success(result);
                }
                catch (JsonException exception)
                {
                    return SystemTrayScriptOutputResult.Failure($"JSON inválido: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return SystemTrayScriptOutputResult.Failure("El script fue cancelado o superó el timeout.");
        }
        catch (Exception exception)
        {
            return SystemTrayScriptOutputResult.Failure($"No se pudo ejecutar el script: {exception.Message}");
        }
    }

    private static string ResolvePath(string value, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return value;
        }

        var candidate = Path.GetFullPath(Path.Combine(workingDirectory, value));
        return File.Exists(candidate) ? candidate : value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // El resultado del script ya informa del timeout; no ocultar el error original.
        }
    }
}
