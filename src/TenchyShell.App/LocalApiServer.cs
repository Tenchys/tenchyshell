using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TenchyShell.Core.LocalApi;
using TenchyShell.Core.Logging;

namespace TenchyShell.App;

internal sealed class LocalApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILocalApiStateProvider state;
    private readonly ILogger logger;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim connectionSlots = new(LocalApiDefaults.MaxConnections);
    private readonly object clientsGate = new();
    private readonly HashSet<Client> clients = new();
    private readonly HashSet<Task> backgroundTasks = new();
    private Task? acceptTask;
    private bool disposed;

    public LocalApiServer(ILocalApiStateProvider state, ILogger logger)
    {
        this.state = state;
        this.logger = logger;
        state.StateChanged += OnStateChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        acceptTask ??= AcceptLoopAsync(cancellation.Token);
        logger.Info($"API local habilitada en el Named Pipe '{LocalApiDefaults.PipeName}'.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        state.StateChanged -= OnStateChanged;
        cancellation.Cancel();
        WaitForAcceptTask();
        lock (clientsGate)
        {
            foreach (var client in clients.ToArray()) client.Dispose();
        }
        WaitForBackgroundTasks();
        connectionSlots.Dispose();
        cancellation.Dispose();
        logger.Info("API local detenida.");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                if (!await connectionSlots.WaitAsync(0, token).ConfigureAwait(false))
                {
                    await WriteRejectedConnectionAsync(pipe, token).ConfigureAwait(false);
                    pipe.Dispose();
                    continue;
                }

                var client = new Client(pipe, state, logger, OnClientClosed);
                lock (clientsGate) clients.Add(client);
                pipe = null;
                Track(client.RunAsync(token), "Error no observado en una conexión de API local.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                pipe?.Dispose();
            }
            catch (Exception exception)
            {
                pipe?.Dispose();
                logger.Error("Falló el servidor de API local; se reintentará.", exception);
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No se pudo obtener el SID del usuario actual.");
        security.AddAccessRule(new PipeAccessRule(identity, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            LocalApiDefaults.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static async Task WriteRejectedConnectionAsync(Stream pipe, CancellationToken token)
    {
        var message = JsonSerializer.Serialize(
            new LocalApiEnvelope(LocalApiDefaults.Version, "response", null, false, null,
                new LocalApiError("connection_limit", "Se alcanzó el límite de conexiones.")), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private void OnStateChanged(object? sender, LocalApiStateChangedEventArgs args)
    {
        Client[] active;
        lock (clientsGate) active = clients.ToArray();
        foreach (var client in active)
        {
            Track(client.PublishAsync(args, cancellation.Token), "Error no observado al publicar un evento de API local.");
        }
    }

    private void OnClientClosed(Client client)
    {
        lock (clientsGate) clients.Remove(client);
        connectionSlots.Release();
    }

    private void Track(Task task, string errorMessage)
    {
        var observed = ObserveAsync(task, errorMessage);
        lock (clientsGate) backgroundTasks.Add(observed);
        _ = observed.ContinueWith(
            completed =>
            {
                lock (clientsGate) backgroundTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveAsync(Task task, string errorMessage)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) { logger.Error(errorMessage, exception); }
    }

    private void WaitForBackgroundTasks()
    {
        Task[] active;
        lock (clientsGate) active = backgroundTasks.ToArray();
        try { Task.WhenAll(active).GetAwaiter().GetResult(); }
        catch (Exception exception) { logger.Error("Falló la espera del cierre de API local.", exception); }
    }

    private void WaitForAcceptTask()
    {
        if (acceptTask is null) return;
        try { acceptTask.GetAwaiter().GetResult(); }
        catch (Exception exception) { logger.Error("Falló la espera de aceptación de API local.", exception); }
    }

    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly ILocalApiStateProvider state;
        private readonly ILogger logger;
        private readonly Action<Client> closed;
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly object topicsGate = new();
        private readonly HashSet<string> topics = new(StringComparer.OrdinalIgnoreCase);
        private int isClosed;

        public Client(NamedPipeServerStream pipe, ILocalApiStateProvider state, ILogger logger, Action<Client> closed)
        {
            this.pipe = pipe;
            this.state = state;
            this.logger = logger;
            this.closed = closed;
        }

        public async Task RunAsync(CancellationToken serverToken)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                while (!serverToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var lineTask = reader.ReadLineAsync(serverToken).AsTask();
                    var line = await lineTask.WaitAsync(TimeSpan.FromMilliseconds(LocalApiDefaults.OperationTimeoutMilliseconds), serverToken).ConfigureAwait(false);
                    if (line is null) break;
                    if (Encoding.UTF8.GetByteCount(line) > LocalApiDefaults.MaxMessageBytes)
                    {
                        await SendErrorAsync(null, "message_too_large", "El mensaje supera el límite permitido.", serverToken).ConfigureAwait(false);
                        break;
                    }
                    await HandleAsync(line, serverToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested) { }
            catch (TimeoutException)
            {
                await SendErrorAsync(null, "timeout", "La solicitud no se completó dentro del timeout.", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.Error("Error en una conexión de API local.", exception);
            }
            finally { Dispose(); }
        }

        public async Task PublishAsync(LocalApiStateChangedEventArgs change, CancellationToken token)
        {
            lock (topicsGate)
            {
                if (!topics.Contains(change.Topic)) return;
            }

            try
            {
                await SendAsync(new LocalApiEnvelope(LocalApiDefaults.Version, "event", null, null, null, null, change.Topic, change.Snapshot), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.Error("Se cerró un cliente de API local durante una publicación.", exception);
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isClosed, 1) != 0) return;
            pipe.Dispose();
            closed(this);
        }

        private async Task HandleAsync(string line, CancellationToken token)
        {
            LocalApiRequest? request;
            try { request = JsonSerializer.Deserialize<LocalApiRequest>(line, JsonOptions); }
            catch (JsonException) { await SendErrorAsync(null, "invalid_request", "JSON inválido.", token); return; }
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Version != LocalApiDefaults.Version)
            {
                await SendErrorAsync(request?.Id, request?.Version == LocalApiDefaults.Version ? "invalid_request" : "unsupported_version", "La solicitud no es válida para el protocolo v1.", token);
                return;
            }

            if (request.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            {
                lock (topicsGate)
                {
                    topics.Clear();
                    foreach (var topic in request.Topics ?? Array.Empty<string>()) topics.Add(topic);
                }
                await SendAsync(new LocalApiEnvelope(LocalApiDefaults.Version, "response", request.Id, true, state.GetSnapshot()), token);
                return;
            }

            if (!request.Type.Equals("request", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Method))
            {
                await SendErrorAsync(request.Id, "invalid_request", "Se requiere type=request y method.", token);
                return;
            }

            switch (request.Method)
            {
                case "workspace.active.get":
                case "workspaces.get":
                case "applications.get":
                case "windows.get":
                    await SendAsync(new LocalApiEnvelope(LocalApiDefaults.Version, "response", request.Id, true, QueryResult(request.Method)), token);
                    break;
                case "workspace.switch":
                    await ExecuteCommandAsync(request, ReadWorkspace(request), state.SwitchWorkspace, token);
                    break;
                case "window.active.move":
                    await ExecuteCommandAsync(request, ReadWorkspace(request), state.MoveActiveWindow, token);
                    break;
                default:
                    await SendErrorAsync(request.Id, "unknown_method", "Método no soportado.", token);
                    break;
            }
        }

        private object QueryResult(string method)
        {
            var snapshot = state.GetSnapshot();
            return method switch
            {
                "workspace.active.get" => new { activeWorkspace = snapshot.ActiveWorkspace },
                "workspaces.get" => snapshot.Workspaces,
                "applications.get" => snapshot.Applications,
                "windows.get" => snapshot.Windows,
                _ => snapshot
            };
        }

        private async Task ExecuteCommandAsync(LocalApiRequest request, int? workspace, Func<int, LocalApiResult> command, CancellationToken token)
        {
            if (!workspace.HasValue)
            {
                await SendErrorAsync(request.Id, "invalid_request", "params.workspace debe ser un entero entre 1 y 9.", token);
                return;
            }
            var result = command(workspace.Value);
            if (result.Succeeded) await SendAsync(new LocalApiEnvelope(LocalApiDefaults.Version, "response", request.Id, true, new { succeeded = true }), token);
            else await SendErrorAsync(request.Id, result.ErrorCode ?? "internal_error", result.ErrorMessage ?? "La operación falló.", token);
        }

        private static int? ReadWorkspace(LocalApiRequest request)
        {
            if (request.Parameters is null || !request.Parameters.TryGetValue("workspace", out var value)) return null;
            if (value is JsonElement element && element.TryGetInt32(out var number)) return number is >= 1 and <= 9 ? number : null;
            return value is int numberValue && numberValue is >= 1 and <= 9 ? numberValue : null;
        }

        private Task SendErrorAsync(string? id, string code, string message, CancellationToken token) =>
            SendAsync(new LocalApiEnvelope(LocalApiDefaults.Version, "response", id, false, null, new LocalApiError(code, message)), token);

        private async Task SendAsync(LocalApiEnvelope envelope, CancellationToken token)
        {
            if (Volatile.Read(ref isClosed) != 0) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions) + "\n");
            if (bytes.Length > LocalApiDefaults.MaxMessageBytes) return;
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref isClosed) != 0) return;
                await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            }
            finally { writeLock.Release(); }
        }
    }
}
