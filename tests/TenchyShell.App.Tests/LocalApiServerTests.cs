using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
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

    [Fact]
    public async Task WriteFailureLogsConnectionAndReleasesSlotForAnotherClient()
    {
        var state = new TestStateProvider();
        var logger = new TestLogger();
        var failWrites = false;
        using var server = new LocalApiServer(state, logger, writeClientMessage: (stream, bytes) =>
        {
            if (Volatile.Read(ref failWrites)) throw new IOException("simulated client disconnect");
            return stream.WriteAsync(bytes).AsTask();
        });
        server.Start();

        var subscribed = await ConnectAndSubscribeAsync("windows", "disconnect-during-publication");
        Volatile.Write(ref failWrites, true);
        state.Publish("windows");
        subscribed.Pipe.Dispose();

        var error = await logger.WaitForErrorAsync(
            "API local: falló ",
            TimeSpan.FromSeconds(5),
            () => { });
        Assert.Matches(@"API local: falló WriteAsync del escritor en la conexión \d+\.", error);

        var accepted = new List<NamedPipeClientStream>();
        try
        {
            for (var index = 0; index < LocalApiDefaults.MaxConnections; index++)
            {
                var client = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(5000);
                accepted.Add(client);
            }
            Assert.Single(logger.Errors, message => message == error);
        }
        finally
        {
            foreach (var client in accepted) client.Dispose();
            await subscribed.DisposeAsync();
        }

        server.Dispose();
        Assert.Single(logger.Errors, message => Regex.IsMatch(message, @"API local: falló WriteAsync del escritor en la conexión \d+\."));
    }

    [Fact]
    public void StateChangeWithoutInterestedSubscribersDoesNotBuildSnapshot()
    {
        var state = new TestStateProvider();
        using var server = new LocalApiServer(state, new TestLogger());
        server.Start();

        state.Publish("windows");

        Assert.Equal(0, state.SnapshotCalls);
    }

    [Fact]
    public async Task InterestedClientsShareOneSnapshotAndTopicFilter()
    {
        var state = new TestStateProvider();
        using var server = new LocalApiServer(state, new TestLogger());
        server.Start();
        await using var windowsOne = await ConnectAndSubscribeAsync("windows", "windows-one");
        await using var windowsTwo = await ConnectAndSubscribeAsync("windows", "windows-two");
        await using var workspace = await ConnectAndSubscribeAsync("workspace", "workspace");

        state.ResetSnapshotCalls();
        state.Publish("windows");

        var first = await windowsOne.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var second = await windowsTwo.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"type\":\"event\"", first);
        Assert.Contains("\"topic\":\"windows\"", second);
        Assert.Equal(1, state.SnapshotCalls);

        var unexpected = workspace.Reader.ReadLineAsync();
        Assert.False(ReferenceEquals(unexpected, await Task.WhenAny(unexpected, Task.Delay(250))));
    }

    private static async Task<TestClient> ConnectAndSubscribeAsync(string topic, string id)
    {
        var pipe = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync($"{{\"version\":1,\"id\":\"{id}\",\"type\":\"subscribe\",\"topics\":[\"{topic}\"]}}");
        Assert.NotNull(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        return new TestClient(pipe, writer, reader);
    }

    private sealed class TestClient : IAsyncDisposable
    {
        public TestClient(NamedPipeClientStream pipe, StreamWriter writer, StreamReader reader) { Pipe = pipe; Writer = writer; Reader = reader; }
        public NamedPipeClientStream Pipe { get; }
        public StreamWriter Writer { get; }
        public StreamReader Reader { get; }
        public ValueTask DisposeAsync()
        {
            Reader.Dispose();
            try { Writer.Dispose(); } catch (ObjectDisposedException) { }
            Pipe.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestStateProvider : ILocalApiStateProvider
    {
        private int snapshotCalls;
        public event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;

        public int SnapshotCalls => Volatile.Read(ref snapshotCalls);
        public LocalApiSnapshot GetSnapshot()
        {
            Interlocked.Increment(ref snapshotCalls);
            return new(1, Array.Empty<LocalApiWorkspace>(), Array.Empty<LocalApiWindow>(), Array.Empty<LocalApiApplication>());
        }

        public LocalApiResult SwitchWorkspace(int workspace) => LocalApiResult.Success();

        public LocalApiResult MoveActiveWindow(int workspace) => LocalApiResult.Success();

        public void Publish(string topic) => StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs(topic));
        public void ResetSnapshotCalls() => Interlocked.Exchange(ref snapshotCalls, 0);
    }

    private sealed class TestLogger : ILogger
    {
        private readonly ConcurrentQueue<string> errors = new();
        private readonly SemaphoreSlim changed = new(0);
        public void Info(string message) { }
        public void Error(string message) { errors.Enqueue(message); changed.Release(); }
        public void Error(string message, Exception exception) { errors.Enqueue(message); changed.Release(); }
        public IReadOnlyCollection<string> Errors => errors.ToArray();

        public async Task<string> WaitForErrorAsync(string prefix, TimeSpan timeout, Action trigger)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                trigger();
                if (errors.FirstOrDefault(message => message.StartsWith(prefix, StringComparison.Ordinal)) is { } match) return match;
                try { await changed.WaitAsync(cancellation.Token); }
                catch (OperationCanceledException) { throw new TimeoutException($"No apareció el error esperado: {prefix}"); }
            }
        }
    }
}

[CollectionDefinition("LocalApiServer", DisableParallelization = true)]
public sealed class LocalApiServerCollection;
