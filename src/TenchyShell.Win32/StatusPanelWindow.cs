using System.Runtime.InteropServices;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Session;
using TenchyShell.Core.Logging;
using TenchyShell.Core.StatusPanel;

namespace TenchyShell.Win32;

/// <summary>
/// Panel informativo nativo que comparte el message loop de TenchyShell.
/// </summary>
public sealed class StatusPanelWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const nuint TimerId = 1;
    private const uint TimerIntervalMilliseconds = 250;
    private static readonly TimeSpan WorkspaceAnnouncementDuration = TimeSpan.FromSeconds(1.5);

    private readonly StatusPanelConfiguration configuration;
    private readonly DockConfiguration dockConfiguration;
    private readonly DockNavigationState dockNavigation;
    private readonly bool customDockConfigured;
    private readonly ILogger logger;
    private readonly DesktopAreaPolicy desktopAreaPolicy;
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly StatusPanelState content = new();
    private readonly StatusPanelClockState clock = new();
    private readonly StatusPanelVisibilityState visibility = new();
    private readonly string windowClassName = $"TenchyShell.StatusPanel.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private bool timerStarted;
    private bool isDisposed;
    private NativeMethods.Rect? cachedWorkArea;
    private StatusPanelRectangle? panelBounds;

    public StatusPanelWindow(StatusPanelConfiguration configuration, ILogger logger, DesktopAreaPolicy? desktopAreaPolicy = null, DockConfiguration? dockConfiguration = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.desktopAreaPolicy = desktopAreaPolicy ?? new DesktopAreaPolicy();
        customDockConfigured = dockConfiguration is not null;
        this.dockConfiguration = dockConfiguration ?? DockConfiguration.CreateDefault();
        dockNavigation = DockNavigationState.FromConfiguration(this.dockConfiguration);
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
        this.desktopAreaPolicy.Changed += InvalidateGeometryCache;
    }

    public bool IsVisible => visibility.IsVisible;

    public event Action? TrayRequested;
    public event Action<string>? DockItemRequested;
    public event Action? Closed;
    public DockNavigationState Navigation => dockNavigation;
    private string? sessionConfirmationText;

    /// <summary>
    /// Suspende el auto-ocultamiento mientras una superficie vinculada al dock
    /// permanezca abierta. No muestra un panel que ya estuviera oculto.
    /// </summary>
    public void SetMenuActive(string menuId, bool isActive)
    {
        TryRun("actualizar el estado de un menú del dock", () =>
        {
            visibility.SetMenuActive(menuId, isActive);
            Invalidate();
        });
    }

    public void Start()
    {
        TryRun("crear el panel informativo", () =>
        {
            EnsureWindow();

            if (!timerStarted)
            {
                var timer = NativeMethods.SetTimer(windowHandle, TimerId, TimerIntervalMilliseconds, IntPtr.Zero);
                if (timer == 0)
                {
                    logger.Error($"No se pudo iniciar el timer del panel informativo. Código Win32: {Marshal.GetLastWin32Error()}.");
                }
                else
                {
                    timerStarted = true;
                }
            }
        });
    }

    public void SetWorkspace(int workspace)
    {
        TryRun("actualizar el workspace del panel informativo", () =>
        {
            content.SetWorkspace(workspace);
            Invalidate();
        });
    }

    /// <summary>Muestra brevemente el dock al cambiar de workspace sin activarlo.</summary>
    public void AnnounceWorkspace(int workspace)
    {
        TryRun("mostrar el workspace activo", () =>
        {
            content.SetWorkspace(workspace);
            if (!ShowWindowAtDock()) return;

            visibility.ShowWorkspaceAnnouncement(DateTimeOffset.UtcNow, WorkspaceAnnouncementDuration);
            Invalidate();
        });
    }

    public void ToggleByHotkey()
    {
        TryRun("alternar el panel informativo", () =>
        {
            EnsureWindow();

            if (visibility.IsVisible && visibility.IsPinnedByHotkey)
            {
                Hide();
                return;
            }

            Show(pinnedByHotkey: true);
        });
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            visibility.Hide();
            desktopAreaPolicy.Changed -= InvalidateGeometryCache;

            if (windowHandle != IntPtr.Zero)
            {
                if (timerStarted)
                {
                    NativeMethods.KillTimer(windowHandle, TimerId);
                    timerStarted = false;
                }

                NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
                NativeMethods.DestroyWindow(windowHandle);
                windowHandle = IntPtr.Zero;
            }

            if (windowClassAtom != 0)
            {
                NativeMethods.UnregisterClass(windowClassName, moduleHandle);
                windowClassAtom = 0;
            }
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo liberar completamente el panel informativo.", exception);
        }
        finally
        {
            isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private void Show(bool pinnedByHotkey)
    {
        if (!ShowWindowAtDock()) return;

        if (pinnedByHotkey)
        {
            visibility.ToggleByHotkey();
        }
        else
        {
            visibility.ShowFromEdge();
        }

        Invalidate();
    }

    private bool ShowWindowAtDock()
    {
        EnsureWindow();
        var workArea = GetPrimaryWorkArea();
        var dockHeight = GetDockHeight();
        var dockWidth = GetDockWidth();
        var x = workArea.Left;
        var y = workArea.Top + Math.Max(0, (workArea.Bottom - workArea.Top - dockHeight) / 2);

        if (!NativeMethods.SetWindowPos(
                windowHandle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                dockWidth,
                dockHeight,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            logger.Error($"No se pudo posicionar el panel informativo. Código Win32: {Marshal.GetLastWin32Error()}.");
            return false;
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOWNOACTIVATE);
        panelBounds = new StatusPanelRectangle(x, y, x + dockWidth, y + dockHeight);
        return true;
    }

    private int GetDockWidth() => customDockConfigured && dockConfiguration.Enabled
        ? Math.Max(configuration.Width, 300)
        : configuration.Width;

    private int GetDockHeight()
    {
        if (!customDockConfigured || !dockConfiguration.Enabled) return configuration.Height;
        var rows = dockNavigation.Categories.Sum(category => 1 + Math.Max(1, category.Items.Count));
        return Math.Max(configuration.Height, 78 + rows * 19);
    }

    private void Hide()
    {
        visibility.Hide();
        Closed?.Invoke();
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
        }
    }

    private void OnTimer()
    {
        if (!visibility.IsVisible)
        {
            if (IsPointerAtLeftEdge())
            {
                Show(pinnedByHotkey: false);
            }

            return;
        }

        if (visibility.HideWhenPointerLeaves(IsPointerInsidePanel(), DateTimeOffset.UtcNow))
        {
            Hide();
            return;
        }

        if (clock.ShouldInvalidate(DateTime.Now))
        {
            Invalidate();
        }
    }

    private bool IsPointerAtLeftEdge()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        var workArea = GetPrimaryWorkArea();
        return StatusPanelEdgeDetector.IsAtLeftEdge(
            new StatusPanelPoint(point.X, point.Y),
            new StatusPanelRectangle(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom),
            configuration.EdgeZone);
    }

    private bool IsPointerInsidePanel()
    {
        if (panelBounds is not { } bounds || !NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        return StatusPanelEdgeDetector.IsInside(
            new StatusPanelPoint(point.X, point.Y),
            bounds);
    }

    private NativeMethods.Rect GetPrimaryWorkArea()
    {
        if (cachedWorkArea is { } cached)
        {
            return cached;
        }

        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            cachedWorkArea = desktopAreaPolicy.UseMonitorArea ? monitorInfo.Monitor : monitorInfo.Work;
            return cachedWorkArea.Value;
        }

        cachedWorkArea = new NativeMethods.Rect
        {
            Left = 0,
            Top = 0,
            Right = NativeMethods.GetSystemMetrics(0),
            Bottom = NativeMethods.GetSystemMetrics(1)
        };
        return cachedWorkArea.Value;
    }

    private void InvalidateGeometryCache() => cachedWorkArea = null;

    private void RefreshGeometryAfterSystemChange()
    {
        InvalidateGeometryCache();
        if (visibility.IsVisible)
        {
            ShowWindowAtDock();
        }
    }

    private void EnsureWindow()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (windowHandle != IntPtr.Zero)
        {
            return;
        }

        var windowClass = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            WindowProcedure = windowProcedure,
            Instance = moduleHandle,
            ClassName = windowClassName
        };

        windowClassAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (windowClassAtom == 0)
        {
            throw new InvalidOperationException($"No se pudo registrar la ventana del panel informativo. Código Win32: {Marshal.GetLastWin32Error()}.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            windowClassName,
            "TenchyShell Status Panel",
            WS_POPUP,
            0,
            0,
            configuration.Width,
            configuration.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
            throw new InvalidOperationException($"No se pudo crear la ventana del panel informativo. Código Win32: {Marshal.GetLastWin32Error()}.");
        }
    }

    private void Invalidate()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case NativeMethods.WM_TIMER:
                    OnTimer();
                    return IntPtr.Zero;
                case NativeMethods.WM_DISPLAYCHANGE:
                case NativeMethods.WM_DPICHANGED:
                case NativeMethods.WM_SETTINGCHANGE:
                    RefreshGeometryAfterSystemChange();
                    return IntPtr.Zero;
                case NativeMethods.WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;
                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);
                case NativeMethods.WM_NCHITTEST:
                    return new IntPtr(NativeMethods.HTCLIENT);
                case NativeMethods.WM_MOUSEACTIVATE:
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);
                case NativeMethods.WM_LBUTTONUP:
                    if (customDockConfigured && dockConfiguration.Enabled)
                    {
                        var clickY = unchecked((short)(lParam.ToInt64() >> 16));
                        var itemSelected = dockNavigation.SelectAt(clickY);
                        Invalidate();
                        if (itemSelected && dockNavigation.SelectedItem is { } item)
                            DockItemRequested?.Invoke(item.Action);
                        return IntPtr.Zero;
                    }
                    TrayRequested?.Invoke();
                    return IntPtr.Zero;
                case NativeMethods.WM_CLOSE:
                    Hide();
                    return IntPtr.Zero;
                case NativeMethods.WM_KEYDOWN:
                    if (wParam.ToInt32() == NativeMethods.VK_ESCAPE)
                    {
                        Hide();
                        return IntPtr.Zero;
                    }
                    if (customDockConfigured && dockConfiguration.Enabled)
                    {
                        switch (wParam.ToInt32())
                        {
                            case NativeMethods.VK_LEFT: dockNavigation.MoveCategory(-1); break;
                            case NativeMethods.VK_RIGHT: dockNavigation.MoveCategory(1); break;
                            case NativeMethods.VK_UP: dockNavigation.MoveItem(-1); break;
                            case NativeMethods.VK_DOWN: dockNavigation.MoveItem(1); break;
                            case NativeMethods.VK_SPACE: dockNavigation.ToggleExpanded(); break;
                            case NativeMethods.VK_RETURN:
                                if (dockNavigation.SelectedItem is { } selected) DockItemRequested?.Invoke(selected.Action);
                                break;
                            default: return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
                        }
                        Invalidate();
                        return IntPtr.Zero;
                    }
                    return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
                default:
                    return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
            }
        }
        catch (Exception exception)
        {
            logger.Error("Error procesando un evento del panel informativo.", exception);
            return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    private void Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        if (deviceContext == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var background = NativeMethods.CreateSolidBrush(0x00202020);
            try
            {
                NativeMethods.FillRect(deviceContext, ref paintStruct.PaintRectangle, background);
            }
            finally
            {
                NativeMethods.DeleteObject(background);
            }

            NativeMethods.SetBkMode(deviceContext, (int)NativeMethods.TRANSPARENT);
            NativeMethods.SetTextColor(deviceContext, 0x00FFFFFF);
            var workspace = content.WorkspaceLabel;
            NativeMethods.TextOut(deviceContext, 18, 20, workspace, workspace.Length);
            var time = content.GetTimeLabel(DateTime.Now);
            NativeMethods.TextOut(deviceContext, 18, 53, time, time.Length);
            if (!string.IsNullOrWhiteSpace(sessionConfirmationText))
            {
                NativeMethods.SetTextColor(deviceContext, 0x0000D0FF);
                NativeMethods.TextOut(deviceContext, 18, 70, sessionConfirmationText, sessionConfirmationText.Length);
            }
            if (customDockConfigured && dockConfiguration.Enabled)
            {
                var y = string.IsNullOrWhiteSpace(sessionConfirmationText) ? 76 : 95;
                foreach (var category in dockNavigation.Categories)
                {
                    var categoryLabel = $"[{category.Title}]";
                    NativeMethods.SetTextColor(deviceContext, 0x00A0D0FF);
                    NativeMethods.TextOut(deviceContext, 18, y, categoryLabel, categoryLabel.Length);
                    y += 19;
                    if (!dockNavigation.Expanded)
                        continue;
                    if (category.Items.Count == 0)
                    {
                        const string empty = "  (vacío)";
                        NativeMethods.SetTextColor(deviceContext, 0x00808080);
                        NativeMethods.TextOut(deviceContext, 18, y, empty, empty.Length);
                        y += 19;
                        continue;
                    }

                    foreach (var item in category.Items)
                    {
                        var selected = ReferenceEquals(category, dockNavigation.SelectedCategory) &&
                                       ReferenceEquals(item, dockNavigation.SelectedItem);
                        var label = $"{(selected ? ">" : " ")} {item.Title}";
                        NativeMethods.SetTextColor(deviceContext, selected ? 0x00FFFFFFu : 0x00D0D0D0u);
                        NativeMethods.TextOut(deviceContext, 18, y, label, label.Length);
                        y += 19;
                    }
                }
            }
            else
            {
                const string trayHint = "Click: bandeja";
                NativeMethods.SetTextColor(deviceContext, 0x00A0A0A0);
                NativeMethods.TextOut(deviceContext, 18, 77, trayHint, trayHint.Length);
            }
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paintStruct);
        }
    }

    public void SetSessionConfirmation(SessionConfirmationStatus status, SessionAction? action)
    {
        sessionConfirmationText = status == SessionConfirmationStatus.AwaitingConfirmation && action is { } pending
            ? $"Confirma {pending}: repite la acción"
            : status == SessionConfirmationStatus.Executing ? "Ejecutando acción de sesión..." : null;
        Invalidate();
    }

    private void TryRun(string operation, Action action)
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            logger.Error($"No se pudo {operation}; TenchyShell continuará activo.", exception);
        }
    }
}
