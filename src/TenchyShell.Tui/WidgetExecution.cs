using System.Diagnostics;
using System.Text;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Win32;

namespace TenchyShell.Tui;

public sealed record WidgetExecutionResult(
    bool Succeeded,
    string Output,
    string? Error = null,
    bool TimedOut = false,
    bool Truncated = false,
    ProcessResourceSnapshot? Resources = null)
{
    public static WidgetExecutionResult Success(string output, bool truncated = false, ProcessResourceSnapshot? resources = null) =>
        new(true, output, null, false, truncated, resources);

    public static WidgetExecutionResult Failure(string error, bool timedOut = false) =>
        new(false, string.Empty, error, timedOut, false);
}

public interface IWidgetCommandRunner
{
    Task<WidgetExecutionResult> ExecuteAsync(
        TuiWidgetConfiguration widget,
        CancellationToken cancellationToken = default);
}

public static class WidgetOutputLimiter
{
    public static (string Output, bool Truncated) Limit(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maximumBytes) return (value, false);
        return (Encoding.UTF8.GetString(bytes, 0, maximumBytes), true);
    }
}

public sealed class ExternalWidgetCommandRunner : IWidgetCommandRunner
{
    public async Task<WidgetExecutionResult> ExecuteAsync(
        TuiWidgetConfiguration widget,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(widget.Command))
        {
            return WidgetExecutionResult.Failure("El comando del widget está vacío.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = widget.Command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in widget.Arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) return WidgetExecutionResult.Failure("Windows no inició el comando del widget.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return WidgetExecutionResult.Failure($"No se pudo iniciar el widget: {exception.Message}");
        }

        var outputTask = ReadLimitedAsync(process.StandardOutput, widget.MaxOutputBytes, cancellationToken);
        var errorTask = ReadLimitedAsync(process.StandardError, widget.MaxOutputBytes, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = process.WaitForExitAsync(timeout.Token);
        var completed = await Task.WhenAny(waitTask, Task.Delay(widget.TimeoutMilliseconds, cancellationToken));
        if (completed != waitTask)
        {
            TryKill(process);
            await AwaitExitQuietly(process);
            return cancellationToken.IsCancellationRequested
                ? WidgetExecutionResult.Failure("Actualización cancelada.")
                : WidgetExecutionResult.Failure("El widget superó su tiempo límite.", timedOut: true);
        }

        await waitTask;
        var output = await outputTask;
        var errorOutput = await errorTask;
        var limited = WidgetOutputLimiter.Limit(output, widget.MaxOutputBytes);
        if (process.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(errorOutput)
                ? $"El widget terminó con código {process.ExitCode}."
                : errorOutput.Trim();
            return WidgetExecutionResult.Failure(error);
        }

        return WidgetExecutionResult.Success(limited.Output, limited.Truncated, ProcessResourceSampler.Capture(process));
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        var buffer = new char[512];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (bytes >= maximumBytes) continue;
            var chunk = new string(buffer, 0, count);
            var chunkBytes = Encoding.UTF8.GetBytes(chunk);
            var remaining = maximumBytes - bytes;
            if (chunkBytes.Length <= remaining)
            {
                builder.Append(chunk);
                bytes += chunkBytes.Length;
            }
            else
            {
                builder.Append(Encoding.UTF8.GetString(chunkBytes, 0, remaining));
                bytes = maximumBytes;
            }
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task AwaitExitQuietly(Process process)
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
    }
}
