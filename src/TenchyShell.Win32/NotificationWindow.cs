using System.Runtime.InteropServices;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Notifications;

namespace TenchyShell.Win32;

/// <summary>Avisos emergentes propios, independientes de Explorer y sin foco.</summary>
public sealed class NotificationWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int Width = 360;
    private const int MinimumCardHeight = 104;
    private const int Margin = 16;
    private const int CardGap = 8;
    private const int CardPadding = 14;
    private const int TextSafetyPadding = 8;
    private const int IconTextLeft = 60;
    private const int MaximumVisibleCards = 3;
    private const nuint ExpirationTimerId = 1;
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(6);

    private readonly NotificationCenter notificationCenter;
    private readonly ILogger logger;
    private readonly MessageLoopHost messageLoop;
    private readonly DesktopAreaPolicy desktopAreaPolicy;
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"TenchyShell.Notifications.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly List<VisibleNotification> cards = new();
    private readonly string iconDirectory;
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private bool disposed;

    public NotificationWindow(
        NotificationCenter notificationCenter,
        ILogger logger,
        MessageLoopHost messageLoop,
        DesktopAreaPolicy desktopAreaPolicy)
    {
        this.notificationCenter = notificationCenter;
        this.logger = logger;
        this.messageLoop = messageLoop;
        this.desktopAreaPolicy = desktopAreaPolicy;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
        iconDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TenchyShell", "state", "notification-icons");
        notificationCenter.Changed += OnNotificationChanged;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        notificationCenter.Changed -= OnNotificationChanged;
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(windowHandle, ExpirationTimerId);
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }
        if (windowClassAtom != 0)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
        }
        try
        {
            if (Directory.Exists(iconDirectory)) Directory.Delete(iconDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo limpiar la caché temporal de iconos de notificaciones.", exception);
        }
        GC.SuppressFinalize(this);
    }

    private void OnNotificationChanged(object? sender, NotificationCenterChangedEventArgs args)
    {
        if (disposed) return;
        messageLoop.Post(() => ApplyNotificationChange(args));
    }

    private void ApplyNotificationChange(NotificationCenterChangedEventArgs args)
    {
        if (disposed) return;
        cards.RemoveAll(card => card.Notification.Id.Equals(args.NotificationId, StringComparison.Ordinal));
        if (args.Added && args.ShowPopup)
        {
            var notification = notificationCenter.GetActive().FirstOrDefault(item => item.Id.Equals(args.NotificationId, StringComparison.Ordinal));
            if (notification is not null && cards.Count < MaximumVisibleCards)
            {
                cards.Add(new VisibleNotification(notification, DateTimeOffset.UtcNow.Add(DisplayDuration), SaveIcon(notification)));
            }
        }
        UpdateWindow();
    }

    private string? SaveIcon(ShellNotification notification)
    {
        if (notification.IconPng is not { Length: > 0 }) return null;
        try
        {
            Directory.CreateDirectory(iconDirectory);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(notification.Id)))[..16];
            var path = Path.Combine(iconDirectory, $"{name}.png");
            File.WriteAllBytes(path, notification.IconPng);
            return path;
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo almacenar temporalmente el icono de una notificación.", exception);
            return null;
        }
    }

    private void UpdateWindow()
    {
        if (cards.Count == 0)
        {
            if (windowHandle != IntPtr.Zero) NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
            return;
        }

        EnsureWindow();
        var area = GetPrimaryArea();
        MeasureCards();
        var height = cards.Sum(card => card.Layout.Height) + (cards.Count - 1) * CardGap;
        var x = area.Right - Width - Margin;
        var y = area.Bottom - height - Margin;
        NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, x, y, Width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOWNOACTIVATE);
        ArmExpirationTimer();
        NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
    }

    private void ArmExpirationTimer()
    {
        if (windowHandle == IntPtr.Zero) return;
        NativeMethods.KillTimer(windowHandle, ExpirationTimerId);
        var next = cards.Min(card => card.ExpiresAt);
        var milliseconds = Math.Max(1, (int)Math.Ceiling((next - DateTimeOffset.UtcNow).TotalMilliseconds));
        NativeMethods.SetTimer(windowHandle, ExpirationTimerId, (uint)milliseconds, IntPtr.Zero);
    }

    private NativeMethods.Rect GetPrimaryArea()
    {
        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return desktopAreaPolicy.UseMonitorArea ? info.Monitor : info.Work;
        }
        return new NativeMethods.Rect { Right = NativeMethods.GetSystemMetrics(0), Bottom = NativeMethods.GetSystemMetrics(1) };
    }

    private void EnsureWindow()
    {
        if (windowHandle != IntPtr.Zero) return;
        var windowClass = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            WindowProcedure = windowProcedure,
            Instance = moduleHandle,
            ClassName = windowClassName
        };
        windowClassAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (windowClassAtom == 0) throw new InvalidOperationException("No se pudo crear la clase de avisos.");
        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            windowClassName,
            "TenchyShell Notifications",
            WS_POPUP,
            0, 0, Width, MinimumCardHeight,
            IntPtr.Zero, IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero) throw new InvalidOperationException("No se pudo crear la ventana de avisos.");
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_TIMER && (nuint)wParam == ExpirationTimerId)
        {
            cards.RemoveAll(card => card.ExpiresAt <= DateTimeOffset.UtcNow);
            UpdateWindow();
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_LBUTTONUP)
        {
            var y = (short)((long)lParam >> 16);
            var top = 0;
            foreach (var card in cards)
            {
                if (y >= top && y < top + card.Layout.Height)
                {
                    notificationCenter.RequestDismiss(card.Notification.Id);
                    break;
                }
                top += card.Layout.Height + CardGap;
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_PAINT) { Paint(hWnd); return IntPtr.Zero; }
        if (message == NativeMethods.WM_ERASEBKGND) return new IntPtr(1);
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        var context = NativeMethods.BeginPaint(hWnd, out var paint);
        var font = NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT);
        var previousFont = font == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(context, font);
        try
        {
            for (var index = 0; index < cards.Count; index++)
            {
                var top = cards.Take(index).Sum(card => card.Layout.Height + CardGap);
                var layout = cards[index].Layout;
                var rect = new NativeMethods.Rect { Left = 0, Top = top, Right = Width, Bottom = top + layout.Height };
                var brush = NativeMethods.CreateSolidBrush(0x00252525);
                NativeMethods.FillRect(context, ref rect, brush);
                NativeMethods.DeleteObject(brush);

                var card = cards[index];
                if (!string.IsNullOrWhiteSpace(card.IconPath)) GdiPlusImage.Draw(context, card.IconPath, Margin, top + CardPadding, 32, 32);
                NativeMethods.SetBkMode(context, (int)NativeMethods.TRANSPARENT);
                var textLeft = layout.TextLeft;
                var textRight = Width - Margin;
                NativeMethods.SetTextColor(context, 0x00A0A0A0);
                var appName = card.Notification.AppName;
                var appRect = new NativeMethods.Rect { Left = textLeft, Top = top + CardPadding, Right = textRight, Bottom = top + CardPadding + layout.AppHeight };
                NativeMethods.DrawText(context, appName, appName.Length, ref appRect, NativeMethods.DT_WORDBREAK);
                NativeMethods.SetTextColor(context, 0x00FFFFFF);
                var title = card.Notification.Title;
                var titleTop = appRect.Bottom + 4;
                var titleRect = new NativeMethods.Rect { Left = textLeft, Top = titleTop, Right = textRight, Bottom = titleTop + layout.TitleHeight };
                NativeMethods.DrawText(context, title, title.Length, ref titleRect, NativeMethods.DT_WORDBREAK);
                NativeMethods.SetTextColor(context, 0x00D0D0D0);
                var bodyTop = titleRect.Bottom + 4;
                var body = card.Notification.Body;
                var bodyRect = new NativeMethods.Rect { Left = textLeft, Top = bodyTop, Right = textRight, Bottom = bodyTop + layout.BodyHeight };
                NativeMethods.DrawText(context, body, body.Length, ref bodyRect, NativeMethods.DT_WORDBREAK);
            }
        }
        finally
        {
            if (previousFont != IntPtr.Zero) NativeMethods.SelectObject(context, previousFont);
            NativeMethods.EndPaint(hWnd, ref paint);
        }
    }

    private void MeasureCards()
    {
        var deviceContext = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            foreach (var card in cards) card.Layout = CardLayout.Minimum;
            return;
        }

        var font = NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT);
        var previousFont = font == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(deviceContext, font);
        try
        {
            foreach (var card in cards)
            {
                var textLeft = string.IsNullOrWhiteSpace(card.IconPath) ? Margin : IconTextLeft;
                var textWidth = Width - textLeft - Margin;
                var appHeight = MeasureTextHeight(deviceContext, card.Notification.AppName, textWidth);
                var titleHeight = MeasureTextHeight(deviceContext, card.Notification.Title, textWidth);
                var bodyHeight = string.IsNullOrEmpty(card.Notification.Body) ? 0 : MeasureTextHeight(deviceContext, card.Notification.Body, textWidth);
                var contentHeight = CardPadding + appHeight + 4 + titleHeight + (bodyHeight > 0 ? 4 + bodyHeight : 0) + CardPadding + TextSafetyPadding;
                card.Layout = new CardLayout(textLeft, appHeight, titleHeight, bodyHeight, Math.Max(MinimumCardHeight, contentHeight));
            }
        }
        finally
        {
            if (previousFont != IntPtr.Zero) NativeMethods.SelectObject(deviceContext, previousFont);
            NativeMethods.DeleteDC(deviceContext);
        }
    }

    private static int MeasureTextHeight(IntPtr deviceContext, string text, int width)
    {
        var rect = new NativeMethods.Rect { Left = 0, Top = 0, Right = width, Bottom = 0 };
        NativeMethods.DrawText(deviceContext, text, text.Length, ref rect, NativeMethods.DT_WORDBREAK | NativeMethods.DT_CALCRECT);
        return Math.Max(16, rect.Bottom - rect.Top);
    }

    private sealed record VisibleNotification(ShellNotification Notification, DateTimeOffset ExpiresAt, string? IconPath)
    {
        public CardLayout Layout { get; set; } = CardLayout.Minimum;
    }

    private readonly record struct CardLayout(int TextLeft, int AppHeight, int TitleHeight, int BodyHeight, int Height)
    {
        public static CardLayout Minimum { get; } = new(IconTextLeft, 16, 16, 16, MinimumCardHeight);
    }
}

internal static class GdiPlusImage
{
    private static readonly object SyncRoot = new();
    private static nuint token;
    private static bool started;

    public static bool Draw(IntPtr deviceContext, string path, int x, int y, int width, int height)
    {
        if (!File.Exists(path) || !EnsureStarted()) return false;
        if (NativeMethods.GdipLoadImageFromFile(path, out var image) != 0 || image == IntPtr.Zero) return false;
        try
        {
            if (NativeMethods.GdipCreateFromHDC(deviceContext, out var graphics) != 0 || graphics == IntPtr.Zero) return false;
            try { return NativeMethods.GdipDrawImageRectI(graphics, image, x, y, width, height) == 0; }
            finally { NativeMethods.GdipDeleteGraphics(graphics); }
        }
        finally { NativeMethods.GdipDisposeImage(image); }
    }

    private static bool EnsureStarted()
    {
        lock (SyncRoot)
        {
            if (started) return true;
            var input = new NativeMethods.GdiplusStartupInput { Version = 1 };
            started = NativeMethods.GdiplusStartup(out token, ref input, out _) == 0;
            return started;
        }
    }
}
