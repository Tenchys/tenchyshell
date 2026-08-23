namespace TenchyShell.Core.Notifications;

/// <summary>Mensajes JSONL versionados entre el bridge empaquetado y el shell.</summary>
public sealed record NotificationBridgeMessage(
    string Type,
    ShellNotification? Notification = null,
    string? NotificationId = null,
    bool Historical = false);

public sealed record NotificationBridgeReceivedEventArgs(ShellNotification Notification, bool Historical);

public static class NotificationBridgeProtocol
{
    public const string PipeName = "TenchyShell.NotificationBridge.v1";
    public const string Notification = "notification";
    public const string Removed = "removed";
    public const string Dismiss = "dismiss";
    public const string Ready = "ready";
}
