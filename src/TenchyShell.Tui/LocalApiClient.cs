using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenchyShell.Core.LocalApi;

namespace TenchyShell.Tui;

public enum LocalApiClientState { Disabled, Connecting, Connected, Unavailable, Faulted }

public interface ILocalApiClient : IAsyncDisposable
{
    LocalApiClientState State { get; }
    LocalApiSnapshot? Snapshot { get; }
    string? Error { get; }
    event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;
    event EventHandler<LocalApiSnapshotEventArgs>? SnapshotReceived;
    event EventHandler<LocalApiSnapshotEventArgs>? UpdateReceived;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<LocalApiSnapshot?> RequestSnapshotAsync(CancellationToken cancellationToken = default);
    Task SubscribeAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed class LocalApiSnapshotEventArgs : EventArgs
{
    public LocalApiSnapshotEventArgs(LocalApiSnapshot snapshot, string? topic = null)
    {
        Snapshot = snapshot;
        Topic = topic;
    }
    public LocalApiSnapshot Snapshot { get; }
    public string? Topic { get; }
}

public sealed class LocalApiStateChangedEventArgs : EventArgs
{
    public LocalApiStateChangedEventArgs(LocalApiClientState state, string? error)
    {
        State = state;
        Error = error;
    }
    public LocalApiClientState State { get; }
    public string? Error { get; }
}

public sealed class NamedPipeLocalApiClient : ILocalApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<LocalApiEnvelope>> pending = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private NamedPipeClientStream? pipe;
    private Task? readerTask;
    private CancellationTokenSource? connectionCancellation;
    private bool disposed;

    public LocalApiClientState State { get; private set; } = LocalApiClientState.Unavailable;
    public LocalApiSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;
    public event EventHandler<LocalApiSnapshotEventArgs>? SnapshotReceived;
    public event EventHandler<LocalApiSnapshotEventArgs>? UpdateReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await DisconnectAsync().ConfigureAwait(false);
        SetState(LocalApiClientState.Connecting, null);
        Error = null;
        connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pipe = new NamedPipeClientStream(".", LocalApiDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(LocalApiDefaults.OperationTimeoutMilliseconds, connectionCancellation.Token).ConfigureAwait(false);
            readerTask = ReadLoopAsync(pipe, connectionCancellation.Token);
            SetState(LocalApiClientState.Connected, null);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            SetState(LocalApiClientState.Unavailable, exception is OperationCanceledException ? "Conexión cancelada." : exception.Message);
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    public async Task<LocalApiSnapshot?> RequestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (State != LocalApiClientState.Connected) return null;
        try
        {
            var active = await RequestAsync("workspace.active.get", cancellationToken).ConfigureAwait(false);
            var workspaces = await RequestAsync("workspaces.get", cancellationToken).ConfigureAwait(false);
            var applications = await RequestAsync("applications.get", cancellationToken).ConfigureAwait(false);
            var windows = await RequestAsync("windows.get", cancellationToken).ConfigureAwait(false);
            var snapshot = new LocalApiSnapshot(
                active.GetProperty("activeWorkspace").GetInt32(),
                workspaces.Deserialize<IReadOnlyList<LocalApiWorkspace>>(JsonOptions) ?? Array.Empty<LocalApiWorkspace>(),
                windows.Deserialize<IReadOnlyList<LocalApiWindow>>(JsonOptions) ?? Array.Empty<LocalApiWindow>(),
                applications.Deserialize<IReadOnlyList<LocalApiApplication>>(JsonOptions) ?? Array.Empty<LocalApiApplication>());
            Snapshot = snapshot;
            SnapshotReceived?.Invoke(this, new LocalApiSnapshotEventArgs(snapshot));
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            SetFaulted(exception.Message);
            return null;
        }
    }

    public async Task SubscribeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync(new LocalApiRequest(LocalApiDefaults.Version, Guid.NewGuid().ToString("N"), "subscribe", Topics: LocalApiCapabilityCatalog.Topics), cancellationToken).ConfigureAwait(false);
            var snapshot = response.Result is JsonElement result
                ? result.Deserialize<LocalApiSnapshot>(JsonOptions)
                : null;
            if (snapshot is null) throw new InvalidDataException("La suscripción no devolvió un snapshot válido.");
            Snapshot = snapshot;
            SnapshotReceived?.Invoke(this, new LocalApiSnapshotEventArgs(snapshot));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidDataException or JsonException or OperationCanceledException)
        {
            SetFaulted(exception.Message);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        connectionCancellation?.Cancel();
        var current = pipe;
        pipe = null;
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        if (readerTask is not null) { try { await readerTask.ConfigureAwait(false); } catch { } }
        readerTask = null;
        connectionCancellation?.Dispose();
        connectionCancellation = null;
        lock (sync)
        {
            foreach (var request in pending.Values) request.TrySetException(new IOException("La conexión se cerró."));
            pending.Clear();
        }
        if (!disposed) SetState(LocalApiClientState.Unavailable, Error);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        writeLock.Dispose();
    }

    private async Task<JsonElement> RequestAsync(string method, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new LocalApiRequest(LocalApiDefaults.Version, Guid.NewGuid().ToString("N"), "request", method), cancellationToken).ConfigureAwait(false);
        if (response.Ok != true) throw new InvalidOperationException(response.Error?.Message ?? "La API rechazó la consulta.");
        return response.Result is JsonElement result ? result : throw new InvalidDataException("Respuesta sin resultado.");
    }

    private async Task<LocalApiEnvelope> SendAsync(LocalApiRequest request, CancellationToken cancellationToken)
    {
        var activePipe = pipe ?? throw new IOException("La API local no está conectada.");
        var source = new TaskCompletionSource<LocalApiEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) pending[request.Id] = source;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await activePipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); await activePipe.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { writeLock.Release(); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LocalApiDefaults.OperationTimeoutMilliseconds);
        try { return await source.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        finally { lock (sync) pending.Remove(request.Id); }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream activePipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(activePipe, new UTF8Encoding(false), false, 4096, true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                var envelope = JsonSerializer.Deserialize<LocalApiEnvelope>(line, JsonOptions);
                if (envelope is null) continue;
                if (envelope.Type.Equals("event", StringComparison.OrdinalIgnoreCase) && envelope.Data is JsonElement data)
                {
                    var snapshot = data.Deserialize<LocalApiSnapshot>(JsonOptions);
                    if (snapshot is not null)
                    {
                        Snapshot = snapshot;
                        UpdateReceived?.Invoke(this, new LocalApiSnapshotEventArgs(snapshot, envelope.Topic));
                    }
                }
                else if (envelope.Id is not null)
                {
                    lock (sync) if (pending.TryGetValue(envelope.Id, out var request)) request.TrySetResult(envelope);
                }
            }
            if (!token.IsCancellationRequested) SetState(LocalApiClientState.Unavailable, "El servidor cerró la conexión.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { SetFaulted(exception.Message); }
    }

    private void SetFaulted(string error) => SetState(LocalApiClientState.Faulted, error);

    private void SetState(LocalApiClientState state, string? error)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke(this, new LocalApiStateChangedEventArgs(state, error));
    }
}
