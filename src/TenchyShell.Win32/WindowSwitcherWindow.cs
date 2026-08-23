using System.Diagnostics;
using System.Runtime.InteropServices;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

public sealed class WindowSwitcherWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"TenchyShell.WindowSwitcher.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly IWorkspaceWindowSource workspaceSource;
    private readonly IWorkspaceWindowService windowService;
    private readonly WindowSwitcherConfiguration configuration;
    private readonly ILogger? logger;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly WindowFocusRestrictionCache focusRestrictionCache = new();
    private IntPtr windowHandle;
    private IntPtr previousForegroundWindow;
    private ushort windowClassAtom;
    private WindowSwitcherState state = new(Array.Empty<WindowSwitcherItem>());
    private bool isVisible;
    private bool isDisposed;

    public WindowSwitcherWindow(
        IWorkspaceWindowSource workspaceSource,
        IWorkspaceWindowService windowService,
        WindowSwitcherConfiguration configuration,
        ILogger? logger = null,
        LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.workspaceSource = workspaceSource;
        this.windowService = windowService;
        this.configuration = configuration;
        this.logger = logger;
        this.benchmarkRecorder = benchmarkRecorder?.IsEnabled == true ? benchmarkRecorder : null;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public bool IsVisible => isVisible;

    public void Toggle()
    {
        if (isVisible)
        {
            Hide(restorePreviousFocus: true);
        }
        else
        {
            Show();
        }
    }

    /// <summary>Inicia o avanza el conmutador reteniendo Alt.</summary>
    public void BeginAltTab(bool backwards)
    {
        if (!isVisible)
        {
            Show();
        }

        state.Move(backwards, GetVisibleItemCount());
        Invalidate();
    }

    public void ConfirmSelection() => Confirm();

    public void CancelSelection() => Hide(restorePreviousFocus: true);

    public void Show()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        workspaceSource.Refresh();
        var workspaceItems = workspaceSource.GetCurrentWorkspaceWindows()
            .Select(handle => new WindowSwitcherItem(handle, windowService.GetWindowTitle(handle)))
            .ToArray();
        focusRestrictionCache.Reconcile(workspaceItems);
        state = new WindowSwitcherState(workspaceItems
            .Select(item => item with { FocusRestricted = focusRestrictionCache.IsRestricted(item.Handle, item.Title) }));
        previousForegroundWindow = windowService.GetForegroundWindow();
        EnsureWindow();

        var title = configuration.TitleFormat.Replace(
            "{workspace}",
            workspaceSource.CurrentWorkspace.ToString(),
            StringComparison.OrdinalIgnoreCase);
        NativeMethods.SetWindowText(windowHandle, title);

        var screenWidth = NativeMethods.GetSystemMetrics(0);
        var screenHeight = NativeMethods.GetSystemMetrics(1);
        var x = Math.Max(0, (screenWidth - configuration.Width) / 2);
        var y = Math.Max(0, (screenHeight - configuration.Height) / 3);

        NativeMethods.SetWindowPos(
            windowHandle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            configuration.Width,
            configuration.Height,
            SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(windowHandle);
        NativeMethods.SetFocus(windowHandle);
        isVisible = true;
        Invalidate();
    }

    public void Hide(bool restorePreviousFocus)
    {
        if (!isVisible || windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
        isVisible = false;

        if (restorePreviousFocus && previousForegroundWindow != IntPtr.Zero)
        {
            windowService.Focus(previousForegroundWindow);
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        Hide(restorePreviousFocus: false);

        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }

        if (windowClassAtom != 0)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
        }

        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void EnsureWindow()
    {
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
            throw new InvalidOperationException("No se pudo registrar la ventana del selector de ventanas.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            windowClassName,
            "TenchyShell Window Switcher",
            WS_POPUP | WS_BORDER,
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
            throw new InvalidOperationException("No se pudo crear la ventana del selector de ventanas.");
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_KEYDOWN:
                HandleKey(wParam.ToInt32());
                return IntPtr.Zero;
            case NativeMethods.WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);
            case NativeMethods.WM_CLOSE:
                Hide(restorePreviousFocus: true);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    private void HandleKey(int virtualKey)
    {
        if (!isVisible)
        {
            return;
        }

        switch (virtualKey)
        {
            case NativeMethods.VK_TAB:
                state.Move(
                    (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0,
                    GetVisibleItemCount());
                Invalidate();
                break;
            case NativeMethods.VK_PRIOR:
                state.MovePage(backwards: true, visibleItemCount: GetVisibleItemCount());
                Invalidate();
                break;
            case NativeMethods.VK_NEXT:
                state.MovePage(backwards: false, visibleItemCount: GetVisibleItemCount());
                Invalidate();
                break;
            case NativeMethods.VK_HOME:
                state.SelectFirst(GetVisibleItemCount());
                Invalidate();
                break;
            case NativeMethods.VK_END:
                state.SelectLast(GetVisibleItemCount());
                Invalidate();
                break;
            case NativeMethods.VK_ESCAPE:
                Hide(restorePreviousFocus: true);
                break;
            case NativeMethods.VK_RETURN:
                Confirm();
                break;
        }
    }

    private void Confirm()
    {
        var selected = state.SelectedItem;

        if (selected is null)
        {
            Hide(restorePreviousFocus: false);
            logger?.Error("El selector no tenía una ventana válida para enfocar.");
            return;
        }

        if (selected.FocusRestricted)
        {
            benchmarkRecorder?.Record("window_switcher_confirmation", new
            {
                selectedHandle = selected.Handle.ToInt64(),
                selectedTitle = selected.Title,
                focused = false,
                failure = WorkspaceFocusFailure.AccessDenied.ToString(),
                skippedKnownRestriction = true
            });
            Hide(restorePreviousFocus: false);
            logger?.Info($"El selector omitió HWND {selected.Handle} ('{selected.Title}') por restricción de foco conocida.");
            if (previousForegroundWindow != IntPtr.Zero) windowService.Focus(previousForegroundWindow);
            return;
        }

        var foregroundBefore = windowService.GetForegroundWindow();
        var stopwatch = Stopwatch.StartNew();
        // El selector todavía posee el foreground aquí. Ocultarlo antes de esta
        // llamada permite a Windows devolver el foco a la ventana anterior y
        // rechazar el cambio solicitado por TenchyShell.
        var focusResult = windowService.Focus(selected.Handle);
        stopwatch.Stop();
        var foregroundAfter = windowService.GetForegroundWindow();
        benchmarkRecorder?.Record("window_switcher_confirmation", new
        {
            selectedHandle = selected.Handle.ToInt64(),
            selectedTitle = selected.Title,
            foregroundBefore = foregroundBefore.ToInt64(),
            foregroundAfter = foregroundAfter.ToInt64(),
            focused = focusResult.Succeeded,
            failure = focusResult.Failure.ToString(),
            durationMs = stopwatch.Elapsed.TotalMilliseconds
        });
        Hide(restorePreviousFocus: false);

        if (focusResult.Succeeded)
        {
            logger?.Info($"Selector enfocó HWND {selected.Handle} ('{selected.Title}').");
            return;
        }

        if (focusResult.Failure == WorkspaceFocusFailure.AccessDenied)
        {
            focusRestrictionCache.Remember(selected.Handle, selected.Title, focusResult.Failure);
            const string message = "Windows restringió el foco de esta ventana por permisos. TenchyShell continuará sin elevación y restaurará la ventana anterior.";
            logger?.Error($"El selector no pudo enfocar HWND {selected.Handle} ('{selected.Title}'): {message}");
            NativeMethods.MessageBox(IntPtr.Zero, message, "TenchyShell — foco restringido", NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
        }
        else
        {
            logger?.Error($"El selector no pudo enfocar HWND {selected.Handle} ('{selected.Title}'); se restaurará el foco anterior.");
        }
        if (previousForegroundWindow != IntPtr.Zero)
        {
            windowService.Focus(previousForegroundWindow);
        }
    }

    private void Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        var background = NativeMethods.CreateSolidBrush(0x00252525);
        var rectangle = new NativeMethods.Rect { Left = 0, Top = 0, Right = configuration.Width, Bottom = configuration.Height };
        NativeMethods.FillRect(deviceContext, ref rectangle, background);
        NativeMethods.DeleteObject(background);
        NativeMethods.SetBkMode(deviceContext, (int)NativeMethods.TRANSPARENT);

        NativeMethods.SetTextColor(deviceContext, 0x00A0A0A0);
        var position = state.Items.Count == 0 ? "0/0" : $"{state.SelectedIndex + 1}/{state.Items.Count}";
        var header = $"Workspace {workspaceSource.CurrentWorkspace} · {position} · Tab siguiente · Shift+Tab anterior · Enter confirmar · Escape cancelar";
        NativeMethods.TextOut(deviceContext, 24, 20, header, header.Length);

        var y = 64;
        var visibleItemCount = GetVisibleItemCount();
        state.EnsureSelectedVisibleForPainting(visibleItemCount);
        foreach (var (item, index) in state.Items
                     .Skip(state.FirstVisibleIndex)
                     .Take(visibleItemCount)
                     .Select((item, index) => (item, index + state.FirstVisibleIndex)))
        {
            NativeMethods.SetTextColor(deviceContext, (uint)(index == state.SelectedIndex ? 0x0000D7FF : item.FocusRestricted ? 0x0000A5FF : 0x00FFFFFF));
            var restriction = item.FocusRestricted ? " — foco restringido por permisos" : string.Empty;
            var line = $"{(index == state.SelectedIndex ? "> " : "  ")}{item.Title}{restriction}";
            NativeMethods.TextOut(deviceContext, 24, y, line, line.Length);
            y += 34;
        }

        if (state.Items.Count == 0)
        {
            const string empty = "No hay ventanas operables en este workspace.";
            NativeMethods.SetTextColor(deviceContext, 0x00A0A0A0);
            NativeMethods.TextOut(deviceContext, 24, y, empty, empty.Length);
        }

        NativeMethods.EndPaint(hWnd, ref paintStruct);
    }

    private int GetVisibleItemCount() => Math.Max(1, (configuration.Height - 84) / 34);

    private void Invalidate()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
        }
    }
}
