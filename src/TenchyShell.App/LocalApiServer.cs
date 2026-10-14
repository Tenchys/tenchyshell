using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.LocalApi;
using TenchyShell.Core.Logging;

namespace TenchyShell.App;

/// <summary>Servidor v1: materializa cada publicación una vez para sus suscriptores.</summary>
internal sealed class LocalApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ILocalApiStateProvider state;
    private readonly ILogger logger;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly Func<Stream, byte[], Task> writeClientMessage;
    private readonly Func<Stream, Task> flushClientMessage;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim connectionSlots = new(LocalApiDefaults.MaxConnections);
    private readonly object clientsGate = new();
    // Ordena subscribe + snapshot inicial respecto de los eventos posteriores.
    private readonly object publicationGate = new();
    private readonly HashSet<Client> clients = new();
    private readonly HashSet<Task> backgroundTasks = new();
    private Task? acceptTask;
    private bool disposed;
    private long snapshotsBuilt;
    private long publishedEvents;
    private long publishedBytes;
    private long slowClientDisconnects;
    private int maximumQueueDepth;
    private long nextClientId;

    public LocalApiServer(ILocalApiStateProvider state, ILogger logger, LiveBenchmarkRecorder? benchmarkRecorder = null, Func<Stream, byte[], Task>? writeClientMessage = null, Func<Stream, Task>? flushClientMessage = null)
    {
        this.state = state;
        this.logger = logger;
        this.benchmarkRecorder = benchmarkRecorder;
        this.writeClientMessage = writeClientMessage ?? ((stream, bytes) => stream.WriteAsync(bytes).AsTask());
        this.flushClientMessage = flushClientMessage ?? (stream => stream.FlushAsync());
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
        lock (clientsGate) foreach (var client in clients.ToArray()) client.Dispose();
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
                var client = new Client(Interlocked.Increment(ref nextClientId), pipe, state, logger, writeClientMessage, flushClientMessage, OnClientClosed, Subscribe, OnQueueUpdated);
                lock (clientsGate) clients.Add(client);
                pipe = null;
                Track(client.RunAsync(token), "Error no observado en una conexión de API local.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { pipe?.Dispose(); }
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
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No se pudo obtener el SID del usuario actual.");
        security.AddAccessRule(new PipeAccessRule(identity, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(LocalApiDefaults.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private static Task WriteRejectedConnectionAsync(Stream pipe, CancellationToken token) =>
        pipe.WriteAsync(Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "response", null, false, null, new LocalApiError("connection_limit", "Se alcanzó el límite de conexiones."))), token).AsTask();

    private void OnStateChanged(object? sender, LocalApiStateChangedEventArgs args)
    {
        lock (publicationGate)
        {
            Client[] interested;
            lock (clientsGate) interested = clients.Where(client => client.IsSubscribedTo(args.Topic)).ToArray();
            if (interested.Length == 0) return;

            // Nunca creamos snapshots en cambios sin un cliente interesado.
            var startedAt = Stopwatch.GetTimestamp();
            var payload = Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "event", null, null, null, null, args.Topic, state.GetSnapshot()));
            Interlocked.Increment(ref snapshotsBuilt);
            Interlocked.Increment(ref publishedEvents);
            Interlocked.Add(ref publishedBytes, (long)payload.Length * interested.Length);
            foreach (var client in interested) client.EnqueuePublication(payload);
            RecordTelemetry("local_api_publication", new { topic = args.Topic, subscribers = interested.Length, snapshotDurationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds });
        }
    }

    private void Subscribe(Client client, IReadOnlyList<string> topics, string requestId)
    {
        lock (publicationGate)
        {
            client.ReplaceTopics(topics);
            client.EnqueueResponse(Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "response", requestId, true, state.GetSnapshot())));
            Interlocked.Increment(ref snapshotsBuilt);
            UpdateTopicInterest();
            RecordTelemetry("local_api_subscription", new { subscribers = SubscriberCounts() });
        }
    }

    private void OnClientClosed(Client client)
    {
        lock (clientsGate)
        {
            if (!clients.Remove(client)) return;
        }
        connectionSlots.Release();
        UpdateTopicInterest();
        RecordTelemetry("local_api_client_closed", new { subscribers = SubscriberCounts() });
    }

    private void UpdateTopicInterest()
    {
        lock (clientsGate)
        {
            state.SetTopicInterest("workspace", clients.Any(client => client.IsSubscribedTo("workspace")));
            state.SetTopicInterest("windows", clients.Any(client => client.IsSubscribedTo("windows")));
            state.SetTopicInterest("applications", clients.Any(client => client.IsSubscribedTo("applications")));
        }
    }

    private void Track(Task task, string errorMessage)
    {
        var observed = ObserveAsync(task, errorMessage);
        lock (clientsGate) backgroundTasks.Add(observed);
        _ = observed.ContinueWith(completed => { lock (clientsGate) backgroundTasks.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    private static byte[] Serialize(LocalApiEnvelope envelope) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions) + "\n");

    private void OnQueueUpdated(int depth, bool slowClientDisconnected)
    {
        if (slowClientDisconnected) Interlocked.Increment(ref slowClientDisconnects);
        while (true)
        {
            var previous = Volatile.Read(ref maximumQueueDepth);
            if (depth <= previous || Interlocked.CompareExchange(ref maximumQueueDepth, depth, previous) == previous) break;
        }
        RecordTelemetry("local_api_queue", null);
    }

    private object SubscriberCounts()
    {
        lock (clientsGate)
        {
            return new { workspace = clients.Count(client => client.IsSubscribedTo("workspace")), windows = clients.Count(client => client.IsSubscribedTo("windows")), applications = clients.Count(client => client.IsSubscribedTo("applications")) };
        }
    }

    private void RecordTelemetry(string eventName, object? details) => benchmarkRecorder?.RecordAtMostEverySampleInterval(eventName, new
    {
        details,
        snapshots = Volatile.Read(ref snapshotsBuilt), events = Volatile.Read(ref publishedEvents), bytes = Volatile.Read(ref publishedBytes),
        slowClientDisconnects = Volatile.Read(ref slowClientDisconnects), maximumQueueDepth = Volatile.Read(ref maximumQueueDepth)
    });

    private sealed class Client : IDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly long connectionId;
        private readonly ILocalApiStateProvider state;
        private readonly ILogger logger;
        private readonly Action<Client> closed;
        private readonly Func<Stream, byte[], Task> writeClientMessage;
        private readonly Func<Stream, Task> flushClientMessage;
        private readonly Action<Client, IReadOnlyList<string>, string> subscribe;
        private readonly Action<int, bool> queueUpdated;
        private readonly object topicsGate = new();
        private readonly HashSet<string> topics = new(StringComparer.OrdinalIgnoreCase);
        private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private int isClosed;

        public Client(long connectionId, NamedPipeServerStream pipe, ILocalApiStateProvider state, ILogger logger, Func<Stream, byte[], Task> writeClientMessage, Func<Stream, Task> flushClientMessage, Action<Client> closed, Action<Client, IReadOnlyList<string>, string> subscribe, Action<int, bool> queueUpdated)
        { this.connectionId = connectionId; this.pipe = pipe; this.state = state; this.logger = logger; this.writeClientMessage = writeClientMessage; this.flushClientMessage = flushClientMessage; this.closed = closed; this.subscribe = subscribe; this.queueUpdated = queueUpdated; }

        public async Task RunAsync(CancellationToken serverToken)
        {
            var writer = WriteLoopAsync();
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                while (!serverToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(serverToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(LocalApiDefaults.OperationTimeoutMilliseconds), serverToken).ConfigureAwait(false);
                    if (line is null) break;
                    if (Encoding.UTF8.GetByteCount(line) > LocalApiDefaults.MaxMessageBytes) { EnqueueResponse(SerializeError(null, "message_too_large", "El mensaje supera el límite permitido.")); break; }
                    Handle(line);
                }
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested) { }
            catch (TimeoutException) { EnqueueResponse(SerializeError(null, "timeout", "La solicitud no se completó dentro del timeout.")); }
            catch (Exception) when (Volatile.Read(ref isClosed) != 0) { }
            catch (Exception exception) { logger.Error($"API local: error de lectura en la conexión {connectionId}.", exception); }
            finally
            {
                // Permite que una respuesta de error ya encolada llegue antes del cierre normal.
                outgoing.Writer.TryComplete();
                await writer.ConfigureAwait(false);
                Dispose();
            }
        }

        public bool IsSubscribedTo(string topic) { lock (topicsGate) return Volatile.Read(ref isClosed) == 0 && topics.Contains(topic); }
        public void ReplaceTopics(IReadOnlyList<string> requested) { lock (topicsGate) { topics.Clear(); foreach (var topic in requested) topics.Add(topic); } }
        public void EnqueuePublication(byte[] payload) => Enqueue(payload, "cola de publicaciones llena; cliente lento desconectado");
        public void EnqueueResponse(byte[] payload) => Enqueue(payload, "cola de respuestas llena; cliente desconectado");

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isClosed, 1) != 0) return;
            outgoing.Writer.TryComplete();
            pipe.Dispose();
            closed(this);
        }

        private void Handle(string line)
        {
            LocalApiRequest? request;
            try { request = JsonSerializer.Deserialize<LocalApiRequest>(line, JsonOptions); }
            catch (JsonException) { EnqueueResponse(SerializeError(null, "invalid_request", "JSON inválido.")); return; }
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Version != LocalApiDefaults.Version)
            { EnqueueResponse(SerializeError(request?.Id, request?.Version == LocalApiDefaults.Version ? "invalid_request" : "unsupported_version", "La solicitud no es válida para el protocolo v1.")); return; }
            if (request.Type.Equals("subscribe", StringComparison.OrdinalIgnoreCase)) { subscribe(this, request.Topics ?? Array.Empty<string>(), request.Id); return; }
            if (!request.Type.Equals("request", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Method)) { EnqueueResponse(SerializeError(request.Id, "invalid_request", "Se requiere type=request y method.")); return; }
            switch (request.Method)
            {
                case "workspace.active.get": case "workspaces.get": case "applications.get": case "windows.get": EnqueueResponse(Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "response", request.Id, true, QueryResult(request.Method)))); break;
                case "workspace.switch": ExecuteCommand(request, state.SwitchWorkspace); break;
                case "window.active.move": ExecuteCommand(request, state.MoveActiveWindow); break;
                default: EnqueueResponse(SerializeError(request.Id, "unknown_method", "Método no soportado.")); break;
            }
        }

        private object QueryResult(string method)
        {
            var snapshot = state.GetSnapshot();
            return method switch { "workspace.active.get" => new { activeWorkspace = snapshot.ActiveWorkspace }, "workspaces.get" => snapshot.Workspaces, "applications.get" => snapshot.Applications, "windows.get" => snapshot.Windows, _ => snapshot };
        }

        private void ExecuteCommand(LocalApiRequest request, Func<int, LocalApiResult> command)
        {
            var workspace = ReadWorkspace(request);
            if (!workspace.HasValue) { EnqueueResponse(SerializeError(request.Id, "invalid_request", "params.workspace debe ser un entero entre 1 y 9.")); return; }
            var result = command(workspace.Value);
            EnqueueResponse(result.Succeeded ? Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "response", request.Id, true, new { succeeded = true })) : SerializeError(request.Id, result.ErrorCode ?? "internal_error", result.ErrorMessage ?? "La operación falló."));
        }

        private void Enqueue(byte[] payload, string reason)
        {
            if (payload.Length > LocalApiDefaults.MaxMessageBytes || Volatile.Read(ref isClosed) != 0) return;
            if (!outgoing.Writer.TryWrite(payload)) { logger.Error($"API local: {reason}."); queueUpdated(outgoing.Reader.Count, true); Dispose(); return; }
            queueUpdated(outgoing.Reader.Count, false);
        }

        private async Task WriteLoopAsync()
        {
            var operation = "WriteAsync";
            try
            {
                await foreach (var bytes in outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    operation = "WriteAsync";
                    await writeClientMessage(pipe, bytes).ConfigureAwait(false);
                    operation = "FlushAsync";
                    await flushClientMessage(pipe).ConfigureAwait(false);
                }
            }
            catch (Exception) when (Volatile.Read(ref isClosed) != 0) { }
            catch (Exception exception) { logger.Error($"API local: falló {operation} del escritor en la conexión {connectionId}.", exception); Dispose(); }
        }

        private static int? ReadWorkspace(LocalApiRequest request)
        {
            if (request.Parameters is null || !request.Parameters.TryGetValue("workspace", out var value)) return null;
            if (value is JsonElement element && element.TryGetInt32(out var number)) return number is >= 1 and <= 9 ? number : null;
            return value is int numberValue && numberValue is >= 1 and <= 9 ? numberValue : null;
        }

        private static byte[] SerializeError(string? id, string code, string message) => Serialize(new LocalApiEnvelope(LocalApiDefaults.Version, "response", id, false, null, new LocalApiError(code, message)));
    }
}
