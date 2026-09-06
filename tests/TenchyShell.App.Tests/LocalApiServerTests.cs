using System.IO.Pipes;
using System.Text;
using TenchyShell.App;
using TenchyShell.Core.LocalApi;
using TenchyShell.Core.Logging;
using Xunit;

namespace TenchyShell.App.Tests;

[Collection("LocalApiServer")]
public sealed class LocalApiServerTests
{
    [Fact]
    public async Task FifthClientReceivesConnectionLimitResponse()
    {
        var state = new TestStateProvider();
        using var server = new LocalApiServer(state, new TestLogger());
        server.Start();
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < LocalApiDefaults.MaxConnections; index++)
            {
                var client = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(5000);
                clients.Add(client);
            }

            using var rejected = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await rejected.ConnectAsync(5000);
            using var reader = new StreamReader(rejected, new UTF8Encoding(false), leaveOpen: true);
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("connection_limit", response ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    [Fact]
    public async Task DisconnectedSubscribedClientDoesNotBreakSubsequentPublicationOrShutdown()
    {
        var state = new TestStateProvider();
        using var server = new LocalApiServer(state, new TestLogger());
        server.Start();

        using (var client = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync("{\"version\":1,\"id\":\"subscribe-1\",\"type\":\"subscribe\",\"topics\":[\"windows\"]}");
            Assert.NotNull(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        state.Publish("windows");
    }

    private sealed class TestStateProvider : ILocalApiStateProvider
    {
        public event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;

        public LocalApiSnapshot GetSnapshot() => new(1, Array.Empty<LocalApiWorkspace>(), Array.Empty<LocalApiWindow>(), Array.Empty<LocalApiApplication>());

        public LocalApiResult SwitchWorkspace(int workspace) => LocalApiResult.Success();

        public LocalApiResult MoveActiveWindow(int workspace) => LocalApiResult.Success();

        public void Publish(string topic) => StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs(topic, GetSnapshot()));
    }

    private sealed class TestLogger : ILogger
    {
        public void Info(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception exception) { }
    }
}

[CollectionDefinition("LocalApiServer", DisableParallelization = true)]
public sealed class LocalApiServerCollection;
