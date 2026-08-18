using System.Runtime.InteropServices;
using MinimalShell.Core.Configuration;
using MinimalShell.Core.Logging;
using MinimalShell.Core.StatusPanel;

namespace MinimalShell.Win32;

/// <summary>
/// Panel informativo nativo que comparte el message loop de MinimalShell.
/// </summary>
public sealed class StatusPanelWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const nuint TimerId = 1;
    private const uint TimerIntervalMilliseconds = 250;

    private readonly StatusPanelConfiguration configuration;
    private readonly ILogger logger;
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly StatusPanelState content = new();
    private readonly StatusPanelVisibilityState visibility = new();
    private readonly string windowClassName = $"MinimalShell.StatusPanel.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private bool timerStarted;
    private bool isDisposed;

    public StatusPanelWindow(StatusPanelConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public bool IsVisible => visibility.IsVisible;

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
        EnsureWindow();
        var workArea = GetPrimaryWorkArea();
        var x = workArea.Left;
        var y = workArea.Top + Math.Max(0, (workArea.Bottom - workArea.Top - configuration.Height) / 2);

        if (!NativeMethods.SetWindowPos(
                windowHandle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                configuration.Width,
                configuration.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            logger.Error($"No se pudo posicionar el panel informativo. Código Win32: {Marshal.GetLastWin32Error()}.");
            return;
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOWNOACTIVATE);

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

    private void Hide()
    {
        visibility.Hide();
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

        if (!visibility.IsPinnedByHotkey && !IsPointerInsidePanel())
        {
            Hide();
            return;
        }

        Invalidate();
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
        if (windowHandle == IntPtr.Zero || !NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        return NativeMethods.GetWindowRect(windowHandle, out var rectangle)
            && StatusPanelEdgeDetector.IsInside(
                new StatusPanelPoint(point.X, point.Y),
                new StatusPanelRectangle(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
    }

    private NativeMethods.Rect GetPrimaryWorkArea()
    {
        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return monitorInfo.Work;
        }

        return new NativeMethods.Rect
        {
            Left = 0,
            Top = 0,
            Right = NativeMethods.GetSystemMetrics(0),
            Bottom = NativeMethods.GetSystemMetrics(1)
        };
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
            "MinimalShell Status Panel",
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
                case NativeMethods.WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;
                case NativeMethods.WM_ERASEBKGND:
                    return new IntPtr(1);
                case NativeMethods.WM_NCHITTEST:
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                case NativeMethods.WM_MOUSEACTIVATE:
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);
                case NativeMethods.WM_CLOSE:
                    Hide();
                    return IntPtr.Zero;
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
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paintStruct);
        }
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
            logger.Error($"No se pudo {operation}; MinimalShell continuará activo.", exception);
        }
    }
}
