using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using TenchyShell.Core.Notifications;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace TenchyShell.NotificationBridge;

/// <summary>
/// Proceso empaquetado que requiere la capacidad UserNotificationListener. No
/// presenta avisos propios: reenvía al shell por el pipe local de la sesión.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim WriterGate = new(1, 1);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        BridgeLog.Info($"Inicio del bridge. Argumentos: {string.Join(' ', args)}.");
        if (args.Any(argument => argument.Equals("--test-toast", StringComparison.OrdinalIgnoreCase)))
        {
            ShowTestToast();
            BridgeLog.Info("Toast propia de diagnóstico enviada.");
            return 0;
        }

        var listener = UserNotificationListener.Current;
        if (args.Any(argument => argument.Equals("--request-access", StringComparison.OrdinalIgnoreCase)))
        {
            var requested = await listener.RequestAccessAsync();
            BridgeLog.Info($"Solicitud explícita de permiso: {requested}.");
            Console.WriteLine($"Permiso de notificaciones: {requested}.");
            return requested == UserNotificationListenerAccessStatus.Allowed ? 0 : 2;
        }

        if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
        {
            var requested = await listener.RequestAccessAsync();
            BridgeLog.Info($"Solicitud inicial de permiso: {requested}.");
            if (requested != UserNotificationListenerAccessStatus.Allowed)
            {
                Console.Error.WriteLine("El bridge no tiene permiso de notificaciones.");
                BridgeLog.Error("El bridge no recibió permiso de notificaciones.");
                return 2;
            }
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            NotificationBridgeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(CancellationToken.None);
        BridgeLog.Info("Conexión local con TenchyShell establecida.");
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        listener.NotificationChanged += (_sender, eventArgs) => _ = HandleNotificationChangeAsync(listener, eventArgs, writer);
        await SendAsync(writer, new NotificationBridgeMessage(NotificationBridgeProtocol.Ready));

        var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        foreach (var notification in existing)
        {
            await SendNotificationAsync(notification, writer, historical: true);
        }

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) return 0;
            if (line.Length > 256 * 1024) continue;
            var message = JsonSerializer.Deserialize<NotificationBridgeMessage>(line, JsonOptions);
            if (message?.Type.Equals(NotificationBridgeProtocol.Dismiss, StringComparison.OrdinalIgnoreCase) == true &&
                uint.TryParse(message.NotificationId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                listener.RemoveNotification(id);
            }
        }
    }

    /// <summary>
    /// Emite una única Toast con la identidad MSIX del bridge. Solo se invoca
    /// manualmente para comprobar el recorrido Windows → bridge → shell.
    /// </summary>
    private static void ShowTestToast()
    {
        var document = new XmlDocument();
        document.LoadXml("""
            <toast><visual><binding template="ToastGeneric">
              <text>TenchyShell · prueba de bridge</text>
              <text>Notificación Toast de prueba enviada por una aplicación empaquetada. Debe aparecer como una tarjeta de TenchyShell y en su historial.</text>
            </binding></visual></toast>
            """);
        ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(document));
    }

    private static async Task HandleNotificationChangeAsync(
        UserNotificationListener listener,
        UserNotificationChangedEventArgs eventArgs,
        StreamWriter writer)
    {
        try
        {
            if (eventArgs.ChangeKind == UserNotificationChangedKind.Added)
            {
                BridgeLog.Info($"Evento de notificación agregado: {eventArgs.UserNotificationId}.");
                var notification = listener.GetNotification(eventArgs.UserNotificationId);
                if (notification is not null) await SendNotificationAsync(notification, writer, historical: false);
            }
            else if (eventArgs.ChangeKind == UserNotificationChangedKind.Removed)
            {
                BridgeLog.Info($"Evento de notificación eliminado: {eventArgs.UserNotificationId}.");
                await SendAsync(writer, new NotificationBridgeMessage(
                    NotificationBridgeProtocol.Removed,
                    NotificationId: eventArgs.UserNotificationId.ToString(CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"No se pudo procesar un cambio de notificación: {exception.Message}");
            BridgeLog.Error("No se pudo procesar un cambio de notificación.", exception);
        }
    }

    private static async Task SendNotificationAsync(UserNotification notification, StreamWriter writer, bool historical)
    {
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        var text = binding?.GetTextElements().Select(element => element.Text).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            ?? Array.Empty<string>();
        var title = text.FirstOrDefault() ?? "Notificación";
        var body = string.Join(Environment.NewLine, text.Skip(1));
        var appInfo = notification.AppInfo;
        var model = new ShellNotification(
            notification.Id.ToString(CultureInfo.InvariantCulture),
            appInfo.AppUserModelId,
            appInfo.DisplayInfo.DisplayName,
            title,
            body,
            notification.CreationTime,
            await ReadLogoAsync(() => appInfo.DisplayInfo.GetLogo(new Size(32, 32))));
        await SendAsync(writer, new NotificationBridgeMessage(NotificationBridgeProtocol.Notification, model, Historical: historical));
    }

    private static async Task<byte[]?> ReadLogoAsync(Func<RandomAccessStreamReference> getLogo)
    {
        try
        {
            var reference = getLogo();
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > ShellNotification.MaximumIconBytes) return null;
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SendAsync(StreamWriter writer, NotificationBridgeMessage message)
    {
        await WriterGate.WaitAsync();
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions));
        }
        finally
        {
            WriterGate.Release();
        }
    }
}

internal static class BridgeLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TenchyShell", "logs", "notification-bridge.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // El bridge no puede interrumpir la recepción de notificaciones por un fallo de log.
        }
    }
}
