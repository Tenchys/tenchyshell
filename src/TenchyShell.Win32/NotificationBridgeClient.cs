using System.IO.Pipes;
using System.Text.Json;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Notifications;

namespace TenchyShell.Win32;

/// <summary>
/// Servidor de named pipe para el bridge MSIX. Espera de forma bloqueante; no
/// consulta ni inicia procesos externos.
/// </summary>
public sealed class NotificationBridgeClient : IDisposable
{
    private readonly ILogger logger;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object writerLock = new();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private StreamWriter? writer;
    private Task? serverTask;
    private bool disposed;

    public NotificationBridgeClient(ILogger logger)
    {
        this.logger = logger;
    }

    public event Action<NotificationBridgeReceivedEventArgs>? NotificationReceived;

    public event Action<string>? NotificationRemoved;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (serverTask is not null) return;
        serverTask = Task.Run(ListenAsync);
        logger.Info("Bridge de notificaciones habilitado; esperando su conexión local.");
    }

    public void Dismiss(string notificationId)
    {
        if (string.IsNullOrWhiteSpace(notificationId) || disposed) return;
        try
        {
            lock (writerLock)
            {
                writer?.WriteLine(JsonSerializer.Serialize(
                    new NotificationBridgeMessage(NotificationBridgeProtocol.Dismiss, NotificationId: notificationId),
                    jsonOptions));
            }
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo solicitar el cierre de la notificación al bridge.", exception);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        lock (writerLock)
        {
            writer?.Dispose();
            writer = null;
        }
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    NotificationBridgeProtocol.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                await ProcessConnectionAsync(pipe, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.Error("El bridge de notificaciones se desconectó; se continuará esperando una nueva conexión.", exception);
            }
        }
    }

    private async Task ProcessConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var connectionWriter = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        lock (writerLock)
        {
            writer = connectionWriter;
        }
        logger.Info("Bridge de notificaciones conectado.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) return;
                if (line.Length > 256 * 1024)
                {
                    logger.Error("El bridge envió un mensaje de notificación demasiado grande; se ignoró.");
                    continue;
                }

                var message = JsonSerializer.Deserialize<NotificationBridgeMessage>(line, jsonOptions);
                if (message is null) continue;
                if (message.Type.Equals(NotificationBridgeProtocol.Notification, StringComparison.OrdinalIgnoreCase) &&
                    message.Notification is not null)
                {
                    NotificationReceived?.Invoke(new NotificationBridgeReceivedEventArgs(message.Notification, message.Historical));
                }
                else if (message.Type.Equals(NotificationBridgeProtocol.Removed, StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(message.NotificationId))
                {
                    NotificationRemoved?.Invoke(message.NotificationId);
                }
            }
        }
        finally
        {
            lock (writerLock)
            {
                if (ReferenceEquals(writer, connectionWriter)) writer = null;
            }
        }
    }
}
