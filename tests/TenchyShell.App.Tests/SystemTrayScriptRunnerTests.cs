using TenchyShell.App;
using TenchyShell.Core.SystemTray;
using Xunit;

namespace TenchyShell.App.Tests;

public sealed class SystemTrayScriptRunnerTests
{
    [Fact]
    public async Task RunAsyncReturnsScriptStderrForANonZeroExitCode()
    {
        var result = await RunCommandAsync("echo controlled-error 1>&2 & exit /b 7");

        Assert.False(result.Succeeded);
        Assert.Contains("controlled-error", result.Error, StringComparison.Ordinal);
        Assert.Equal(SystemTrayScriptExecutionOutcome.ProcessError, result.Outcome);
    }

    [Fact]
    public async Task RunAsyncDrainsLargeStandardOutputAndStandardErrorWithoutDeadlocking()
    {
        var result = await RunCommandAsync(
            "(for /L %i in (1,1,4000) do @echo 0123456789) & (for /L %i in (1,1,4000) do @echo 0123456789 1>&2)");

        Assert.False(result.Succeeded);
        Assert.Equal("La salida JSON del script supera 32 KiB.", result.Error);
    }

    [Fact]
    public async Task RunAsyncCancelsAndReturnsWithoutWaitingForTheChildCommand()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var startedAt = DateTimeOffset.UtcNow;

        var result = await RunCommandAsync("ping -n 30 127.0.0.1 > nul", cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("cancelado", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SystemTrayScriptExecutionOutcome.Cancelled, result.Outcome);
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsyncAppliesTheItemTimeoutWhenTheCallerDoesNotCancel()
    {
        var startedAt = DateTimeOffset.UtcNow;

        var result = await RunCommandAsync("ping -n 30 127.0.0.1 > nul", timeoutMilliseconds: 200);

        Assert.False(result.Succeeded);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SystemTrayScriptExecutionOutcome.TimedOut, result.Outcome);
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(5));
    }

    private static Task<SystemTrayScriptOutputResult> RunCommandAsync(
        string command,
        CancellationToken cancellationToken = default,
        int timeoutMilliseconds = 1500)
    {
        var item = new SystemTrayItemConfiguration
        {
            Id = "controlled-test",
            Title = "Controlled test",
            Command = "cmd.exe",
            Arguments = ["/d", "/s", "/c", command],
            TimeoutMilliseconds = timeoutMilliseconds
        };

        return new SystemTrayScriptRunner().RunAsync(item, Environment.CurrentDirectory, cancellationToken);
    }
}
