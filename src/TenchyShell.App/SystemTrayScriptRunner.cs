using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.SystemTray;
using TenchyShell.Win32;

namespace TenchyShell.App;

public sealed class SystemTrayScriptRunner : ISystemTrayScriptRunner
{
    private const int MaximumOutputBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;

    public SystemTrayScriptRunner(LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.benchmarkRecorder = benchmarkRecorder;
    }

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
        using var process = new Process
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

        // The service also owns its refresh lifetime, but keeping a runner-local
        // deadline lets its outcome distinguish a configured timeout from an
        // external cancellation (close, disposal, or superseding refresh).
        using var executionTimeout = new CancellationTokenSource(item.TimeoutMilliseconds);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionTimeout.Token);
        var executionToken = executionCancellation.Token;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                benchmarkRecorder?.RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics.Completed(
                    "system_tray", succeeded: false, durationMilliseconds: stopwatch.ElapsedMilliseconds));
                return SystemTrayScriptOutputResult.Failure(
                    $"No se pudo iniciar '{item.Command}'.",
                    SystemTrayScriptExecutionOutcome.StartFailed);
            }

            using (executionToken.Register(() => TryKill(process)))
            {
                // Draining both pipes continues after the retained output reaches
                // its cap, so a noisy child cannot block on either redirected pipe.
                var outputTask = ReadLimitedAsync(process.StandardOutput, MaximumOutputBytes);
                var errorTask = ReadLimitedAsync(process.StandardError, MaximumOutputBytes);
                await process.WaitForExitAsync(executionToken).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    RecordCompletedMetrics(false);
                    return SystemTrayScriptOutputResult.Failure(
                        string.IsNullOrWhiteSpace(error)
                            ? $"El script terminó con código {process.ExitCode}."
                            : error.Trim(),
                        SystemTrayScriptExecutionOutcome.ProcessError);
                }

                if (output.Truncated)
                {
                    RecordCompletedMetrics(false);
                    return SystemTrayScriptOutputResult.Failure(
                        "La salida JSON del script supera 32 KiB.",
                        SystemTrayScriptExecutionOutcome.InvalidOutput);
                }

                try
                {
                    var result = JsonSerializer.Deserialize<SystemTrayScriptOutput>(output.Value, JsonOptions);
                    if (result is null)
                    {
                        RecordCompletedMetrics(false);
                        return SystemTrayScriptOutputResult.Failure(
                            "El script devolvió JSON vacío.",
                            SystemTrayScriptExecutionOutcome.InvalidOutput);
                    }
                    RecordCompletedMetrics(true);
                    return SystemTrayScriptOutputResult.Success(result);
                }
                catch (JsonException exception)
                {
                    RecordCompletedMetrics(false);
                    return SystemTrayScriptOutputResult.Failure(
                        $"JSON inválido: {exception.Message}",
                        SystemTrayScriptExecutionOutcome.InvalidOutput);
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await AwaitExitQuietly(process).ConfigureAwait(false);
            benchmarkRecorder?.RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics.Completed(
                "system_tray",
                succeeded: false,
                cancelled: !executionTimeout.IsCancellationRequested,
                timedOut: executionTimeout.IsCancellationRequested,
                durationMilliseconds: stopwatch.ElapsedMilliseconds));
            return SystemTrayScriptOutputResult.Failure(
                "El script fue cancelado o superó el timeout.",
                executionTimeout.IsCancellationRequested
                    ? SystemTrayScriptExecutionOutcome.TimedOut
                    : SystemTrayScriptExecutionOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            benchmarkRecorder?.RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics.Completed(
                "system_tray", succeeded: false, durationMilliseconds: stopwatch.ElapsedMilliseconds));
            return SystemTrayScriptOutputResult.Failure(
                $"No se pudo ejecutar el script: {exception.Message}",
                SystemTrayScriptExecutionOutcome.ExecutionError);
        }

        void RecordCompletedMetrics(bool succeeded)
        {
            var resources = ProcessResourceSampler.Capture(process);
            benchmarkRecorder?.RecordPeriodicExecutionMetrics(
                PeriodicExecutionMetrics.Completed(
                    "system_tray",
                    succeeded,
                    cpuMilliseconds: resources.CpuMilliseconds,
                    readBytes: resources.ReadBytes,
                    writtenBytes: resources.WrittenBytes,
                    peakWorkingSetBytes: resources.PeakWorkingSetBytes,
                    durationMilliseconds: stopwatch.ElapsedMilliseconds));
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

    private static async Task<LimitedOutput> ReadLimitedAsync(StreamReader reader, int maximumBytes)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        var truncated = false;
        var buffer = new char[512];

        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) break;

            var chunk = new string(buffer, 0, count);
            var chunkBytes = Encoding.UTF8.GetBytes(chunk);
            var remaining = maximumBytes - bytes;
            if (remaining <= 0)
            {
                truncated = true;
            }
            else if (chunkBytes.Length <= remaining)
            {
                builder.Append(chunk);
                bytes += chunkBytes.Length;
            }
            else
            {
                builder.Append(Encoding.UTF8.GetString(chunkBytes, 0, remaining));
                bytes = maximumBytes;
                truncated = true;
            }
        }

        return new LimitedOutput(builder.ToString(), truncated);
    }

    private static async Task AwaitExitQuietly(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process was not started.
        }
    }

    private sealed record LimitedOutput(string Value, bool Truncated)
    {
        public string Trim() => Value.Trim();

        public static implicit operator string(LimitedOutput output) => output.Value;
    }
}
