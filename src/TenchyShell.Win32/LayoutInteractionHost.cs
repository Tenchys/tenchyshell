using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Layout;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Windows;

namespace TenchyShell.Win32;

/// <summary>
/// Coordina el arrastre con Ctrl+Shift sin crear un segundo message loop.
/// Los callbacks de los hooks solo recopilan estado y publican el trabajo de
/// ventanas en el message loop principal.
/// </summary>
public sealed class LayoutInteractionHost : IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly IWindowService windowService;
    private readonly LayoutZoneCatalog zoneCatalog;
    private readonly ILogger logger;
    private readonly DesktopAreaPolicy desktopAreaPolicy;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly LayoutDragStateMachine dragState = new();
    private readonly LayoutHookInputState inputState = new();
    private readonly LayoutOverlayWindow overlay;
    private readonly NativeMethods.LowLevelHookProc mouseHookProcedure;
    private readonly NativeMethods.LowLevelHookProc keyboardHookProcedure;
    private readonly IntPtr moduleHandle;

    private IntPtr mouseHookHandle;
    private IntPtr keyboardHookHandle;
    private WindowRect currentWorkArea;
    private IReadOnlyList<LayoutZone> currentZones = Array.Empty<LayoutZone>();
    private string currentMonitorId = string.Empty;
    private bool currentMonitorIsPrimary;
    private LayoutHookMetrics? hookMetrics;
    private bool isStarted;
    private bool isDisposed;

    public LayoutInteractionHost(
        MessageLoopHost messageLoop,
        IWindowService windowService,
        LayoutZoneCatalog zoneCatalog,
        double zoneNumberSizePercent,
        ILogger logger,
        DesktopAreaPolicy? desktopAreaPolicy = null,
        LiveBenchmarkRecorder? benchmarkRecorder = null)
    {
        this.messageLoop = messageLoop;
        this.windowService = windowService;
        this.zoneCatalog = zoneCatalog;
        this.logger = logger;
        this.desktopAreaPolicy = desktopAreaPolicy ?? new DesktopAreaPolicy();
        this.benchmarkRecorder = benchmarkRecorder;
        overlay = new LayoutOverlayWindow(logger, zoneNumberSizePercent);
        mouseHookProcedure = OnMouseHook;
        keyboardHookProcedure = OnKeyboardHook;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public void Start()
    {
        if (isStarted || isDisposed)
        {
            return;
        }

        try
        {
            mouseHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                mouseHookProcedure,
                moduleHandle,
                0);

            if (mouseHookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx no registró WH_MOUSE_LL.");
            }

            keyboardHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                keyboardHookProcedure,
                moduleHandle,
                0);

            if (keyboardHookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx no registró WH_KEYBOARD_LL.");
            }

            isStarted = true;
            logger.Info("Overlay de layout habilitado para arrastres con Ctrl+Shift.");
        }
        catch (Exception exception)
        {
            logger.Error("No se pudieron registrar los hooks del overlay de layout; se mantiene el shell activo.", exception);
            Console.Error.WriteLine($"No se pudo habilitar el overlay de layout: {exception.Message}");
            ReleaseHooks();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        dragState.Cancel();
        messageLoop.Post(overlay.Hide);
        ReleaseHooks();
        if (benchmarkRecorder?.IsEnabled == true && hookMetrics is not null)
        {
            benchmarkRecorder.RecordLayoutHookMetrics(hookMetrics.Snapshot());
        }
        overlay.Dispose();
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr OnMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        var metrics = code >= 0 ? GetHookMetrics() : null;
        var startedAt = metrics is null ? 0 : Stopwatch.GetTimestamp();
        var wasDragging = dragState.IsDragging;
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.LowLevelMouseHookData>(lParam);
                switch (unchecked((uint)wParam.ToInt64()))
                {
                    case NativeMethods.WM_LBUTTONDOWN:
                        if (inputState.ControlPressed || inputState.ShiftPressed ||
                            IsModifierPressed(NativeMethods.VK_CONTROL) ||
                            IsModifierPressed(NativeMethods.VK_SHIFT))
                        {
                            logger.Info($"Mouse izquierdo recibido con modificadores: Ctrl={IsControlPressed()}, Shift={IsShiftPressed()}.");
                        }

                        BeginDrag(data.Point);
                        break;
                    case NativeMethods.WM_MOUSEMOVE:
                        UpdateDrag(data.Point);
                        break;
                    case NativeMethods.WM_LBUTTONUP:
                        CompleteDrag();
                        break;
                }
            }
            catch (Exception exception)
            {
                metrics?.RecordError();
                logger.Error("Error procesando el hook de mouse del layout.", exception);
                CancelDrag();
            }
            finally
            {
                metrics?.RecordCallback(
                    LayoutHookType.Mouse,
                    wasDragging || dragState.IsDragging,
                    Stopwatch.GetElapsedTime(startedAt));
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        var metrics = code >= 0 ? GetHookMetrics() : null;
        var startedAt = metrics is null ? 0 : Stopwatch.GetTimestamp();
        var wasDragging = dragState.IsDragging;
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardHookData>(lParam);
                var message = unchecked((uint)wParam.ToInt64());
                var isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

                inputState.ApplyModifier(data.VirtualKeyCode, isKeyDown, isKeyUp);

                if (inputState.ShouldCancelForEscape(data.VirtualKeyCode, isKeyDown, dragState.IsDragging))
                {
                    CancelDrag();
                }
                else if (inputState.ShouldCancelForModifierRelease(data.VirtualKeyCode, isKeyUp, dragState.IsDragging))
                {
                    CancelDrag();
                }
            }
            catch (Exception exception)
            {
                metrics?.RecordError();
                logger.Error("Error procesando el hook de teclado del layout.", exception);
                CancelDrag();
            }
            finally
            {
                metrics?.RecordCallback(
                    LayoutHookType.Keyboard,
                    wasDragging || dragState.IsDragging,
                    Stopwatch.GetElapsedTime(startedAt));
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void BeginDrag(NativeMethods.Point point)
    {
        if (!IsControlPressed() || !IsShiftPressed())
        {
            return;
        }

        if (!windowService.TryGetWindowAtPoint(point.X, point.Y, out var targetWindow, out var pointError) &&
            !windowService.TryGetActiveWindow(out targetWindow, out var activeError))
        {
            logger.Error($"No se encontró una ventana válida bajo el cursor ({point.X},{point.Y}) ni una ventana activa. Punto: {pointError}; activa: {activeError}");
            return;
        }

        if (!TryGetMonitorAtPoint(point, out var workArea, out var monitorId, out var isPrimary))
        {
            logger.Error($"No se pudo identificar el monitor del inicio del arrastre en ({point.X},{point.Y}).");
            return;
        }

        if (!dragState.Begin(targetWindow))
        {
            return;
        }

        currentWorkArea = workArea;
        currentMonitorId = monitorId;
        currentMonitorIsPrimary = isPrimary;
        currentZones = zoneCatalog.GetZonesForMonitor(monitorId, isPrimary);
        dragState.SetHoveredZone(GetZoneAt(point, currentWorkArea, currentZones));
        logger.Info($"Arrastre de layout iniciado sobre HWND {targetWindow} en monitor '{monitorId}'.");
        PostOverlayUpdate();
    }

    private void UpdateDrag(NativeMethods.Point point)
    {
        if (!dragState.IsDragging ||
            !TryGetMonitorAtPoint(point, out var workArea, out var monitorId, out var isPrimary))
        {
            return;
        }

        var monitorChanged = currentWorkArea != workArea ||
                             !string.Equals(currentMonitorId, monitorId, StringComparison.OrdinalIgnoreCase);
        var previousZone = dragState.HoveredZone;
        currentWorkArea = workArea;
        currentMonitorId = monitorId;
        currentMonitorIsPrimary = isPrimary;
        currentZones = zoneCatalog.GetZonesForMonitor(monitorId, isPrimary);
        dragState.SetHoveredZone(GetZoneAt(point, currentWorkArea, currentZones));
        if (monitorChanged || previousZone != dragState.HoveredZone)
        {
            PostOverlayUpdate();
        }
    }

    private void CompleteDrag()
    {
        if (!dragState.TryComplete(out var targetWindow, out var zoneNumber))
        {
            CancelDrag();
            return;
        }

        var zone = currentZones.FirstOrDefault(candidate => candidate.Number == zoneNumber);
        if (zone.Number != zoneNumber)
        {
            PostOverlayHide();
            return;
        }

        var workArea = currentWorkArea;
        var targetRect = LayoutZoneCalculator.ToWindowRect(zone, workArea);
        messageLoop.Post(() =>
        {
            overlay.Hide();
            var result = windowService.PlaceWindow(targetWindow, targetRect);
            if (!result.Succeeded)
            {
                logger.Error($"No se pudo colocar la ventana en la zona {zoneNumber} mediante arrastre: {result.Error}");
            }
        });
    }

    private void CancelDrag()
    {
        if (!dragState.IsDragging)
        {
            return;
        }

        dragState.Cancel();
        hookMetrics?.RecordCancellation();
        PostOverlayHide();
    }

    private void PostOverlayUpdate()
    {
        var workArea = currentWorkArea;
        var zones = currentZones.ToArray();
        var selectedZone = dragState.HoveredZone;
        if (messageLoop.TryPost(() =>
        {
            if (dragState.IsDragging)
            {
                overlay.Show(workArea, zones, selectedZone);
            }
            else
            {
                overlay.Hide();
            }
        }))
        {
            hookMetrics?.RecordOverlayUpdate();
        }
    }

    private void PostOverlayHide() => messageLoop.Post(overlay.Hide);

    private static int? GetZoneAt(
        NativeMethods.Point point,
        WindowRect workArea,
        IReadOnlyList<LayoutZone> zones)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return null;
        }

        var normalizedX = (point.X - workArea.Left) / (double)workArea.Width;
        var normalizedY = (point.Y - workArea.Top) / (double)workArea.Height;
        return LayoutZoneCalculator.TryGetZoneAt(zones, normalizedX, normalizedY, out var zone)
            ? zone.Number
            : null;
    }

    private static bool IsModifierPressed(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private bool IsControlPressed() =>
        inputState.ControlPressed ||
        IsModifierPressed(NativeMethods.VK_CONTROL) ||
        IsModifierPressed(NativeMethods.VK_LCONTROL) ||
        IsModifierPressed(NativeMethods.VK_RCONTROL);

    private bool IsShiftPressed() =>
        inputState.ShiftPressed ||
        IsModifierPressed(NativeMethods.VK_SHIFT) ||
        IsModifierPressed(NativeMethods.VK_LSHIFT) ||
        IsModifierPressed(NativeMethods.VK_RSHIFT);

    private bool TryGetMonitorAtPoint(
        NativeMethods.Point point,
        out WindowRect workArea,
        out string monitorId,
        out bool isPrimary)
    {
        workArea = default;
        monitorId = string.Empty;
        isPrimary = false;

        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var area = desktopAreaPolicy.UseMonitorArea ? info.Monitor : info.Work;
        workArea = new WindowRect(area.Left, area.Top, area.Right, area.Bottom);
        monitorId = info.DeviceName;
        isPrimary = (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
        return workArea.Width > 0 && workArea.Height > 0;
    }

    private void ReleaseHooks()
    {
        if (mouseHookHandle != IntPtr.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(mouseHookHandle))
            {
                logger.Error($"No se pudo liberar WH_MOUSE_LL. Código Win32: {Marshal.GetLastWin32Error()}.");
            }

            mouseHookHandle = IntPtr.Zero;
        }

        if (keyboardHookHandle != IntPtr.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(keyboardHookHandle))
            {
                logger.Error($"No se pudo liberar WH_KEYBOARD_LL. Código Win32: {Marshal.GetLastWin32Error()}.");
            }

            keyboardHookHandle = IntPtr.Zero;
        }

        isStarted = false;
    }

    private LayoutHookMetrics? GetHookMetrics()
    {
        if (benchmarkRecorder?.IsEnabled != true)
        {
            return null;
        }

        return hookMetrics ??= new LayoutHookMetrics();
    }
}
