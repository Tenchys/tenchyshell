namespace TenchyShell.Core.Notifications;

using TenchyShell.Core.Diagnostics;

/// <summary>Representación acotada de una notificación de una aplicación Windows.</summary>
public sealed record ShellNotification(
    string Id,
    string AppId,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    byte[]? IconPng = null)
{
    public const int MaximumIconBytes = 128 * 1024;

    public ShellNotification Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        return this with
        {
            AppId = Limit(AppId, 256, "Aplicación desconocida"),
            AppName = Limit(AppName, 128, "Aplicación desconocida"),
            Title = Limit(Title, 256, "Notificación"),
            Body = Limit(Body, 1024, string.Empty),
            IconPng = IconPng is { Length: > MaximumIconBytes } ? null : IconPng
        };
    }

    private static string Limit(string? value, int maximum, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }
}

public sealed record NotificationCenterChangedEventArgs(string NotificationId, bool Added, bool ShowPopup = false);

/// <summary>
/// Historial de sesión sin persistencia. Las notificaciones eliminadas dejan de
/// aparecer tanto en el historial como en las superficies emergentes.
/// </summary>
public sealed class NotificationCenter
{
    public const int MaximumHistoryItems = 100;

    private readonly object syncRoot = new();
    private readonly Dictionary<string, ShellNotification> items = new(StringComparer.Ordinal);
    private readonly NotificationCenterMetrics? metrics;

    public NotificationCenter(NotificationCenterMetrics? metrics = null) => this.metrics = metrics is { IsEnabled: true } ? metrics : null;

    public event EventHandler<NotificationCenterChangedEventArgs>? Changed;

    public event Action<string>? DismissRequested;

    public IReadOnlyList<ShellNotification> GetActive()
    {
        if (metrics is null)
        {
            lock (syncRoot)
            {
                return items.Values.OrderByDescending(item => item.CreatedAt).ToArray();
            }
        }

        var started = metrics.StartTimestamp();
        var allocated = metrics.StartAllocationCount();
        lock (syncRoot)
        {
            try
            {
                return items.Values.OrderByDescending(item => item.CreatedAt).ToArray();
            }
            finally
            {
                metrics.RecordGetActive(started, allocated);
            }
        }
    }

    public void Add(ShellNotification notification, bool showPopup = true)
    {
        if (metrics is null)
        {
            AddCore(notification, showPopup, out _);
            return;
        }

        var started = metrics.StartTimestamp();
        var allocated = metrics.StartAllocationCount();
        var trimmed = 0;
        try
        {
            AddCore(notification, showPopup, out trimmed);
        }
        finally
        {
            metrics.RecordAdd(started, allocated, trimmed);
        }
    }

    private void AddCore(ShellNotification notification, bool showPopup, out int trimmed)
    {
        trimmed = 0;
        var normalized = notification.Normalize();
        lock (syncRoot)
        {
            items[normalized.Id] = normalized;
            foreach (var expired in items.Values
                         .OrderByDescending(item => item.CreatedAt)
                         .Skip(MaximumHistoryItems)
                         .Select(item => item.Id)
                         .ToArray())
            {
                if (items.Remove(expired)) trimmed++;
            }
        }
        Changed?.Invoke(this, new NotificationCenterChangedEventArgs(normalized.Id, Added: true, ShowPopup: showPopup));
    }

    public void Remove(string notificationId)
    {
        if (metrics is null)
        {
            RemoveCore(notificationId);
            return;
        }

        var started = metrics.StartTimestamp();
        var allocated = metrics.StartAllocationCount();
        try
        {
            RemoveCore(notificationId);
        }
        finally
        {
            metrics.RecordRemove(started, allocated);
        }
    }

    private void RemoveCore(string notificationId)
    {
        if (string.IsNullOrWhiteSpace(notificationId)) return;
        var removed = false;
        lock (syncRoot)
        {
            removed = items.Remove(notificationId);
        }
        if (removed) Changed?.Invoke(this, new NotificationCenterChangedEventArgs(notificationId, Added: false));
    }

    public void RequestDismiss(string notificationId)
    {
        if (string.IsNullOrWhiteSpace(notificationId)) return;
        DismissRequested?.Invoke(notificationId);
    }
}
