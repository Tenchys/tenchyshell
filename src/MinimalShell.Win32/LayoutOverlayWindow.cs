using System.Runtime.InteropServices;
using MinimalShell.Core.Layout;
using MinimalShell.Core.Logging;
using MinimalShell.Core.Windows;

namespace MinimalShell.Win32;

internal sealed class LayoutOverlayWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;

    private readonly ILogger logger;
    private readonly double zoneNumberSizePercent;
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"MinimalShell.LayoutOverlay.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private WindowRect workArea;
    private IReadOnlyList<LayoutZone> zones = Array.Empty<LayoutZone>();
    private int? selectedZone;
    private bool isVisible;
    private bool isDisposed;

    public LayoutOverlayWindow(ILogger logger, double zoneNumberSizePercent)
    {
        this.logger = logger;
        this.zoneNumberSizePercent = zoneNumberSizePercent;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public void Show(WindowRect workArea, IReadOnlyList<LayoutZone> zones, int? selectedZone)
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            EnsureWindow();
            var wasVisible = isVisible;
            var geometryChanged = !wasVisible ||
                                  this.workArea != workArea ||
                                  !this.zones.SequenceEqual(zones);
            var selectionChanged = this.selectedZone != selectedZone;

            this.workArea = workArea;
            this.zones = zones.ToArray();
            this.selectedZone = selectedZone;

            if (geometryChanged)
            {
                NativeMethods.SetWindowPos(
                    windowHandle,
                    NativeMethods.HWND_TOPMOST,
                    workArea.Left,
                    workArea.Top,
                    workArea.Width,
                    workArea.Height,
                    NativeMethods.SWP_NOACTIVATE | (wasVisible ? 0u : NativeMethods.SWP_SHOWWINDOW));
            }

            if (!wasVisible)
            {
                NativeMethods.SetLayeredWindowAttributes(windowHandle, 0, 96, NativeMethods.LWA_ALPHA);
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOWNOACTIVATE);
                isVisible = true;
            }

            if (geometryChanged || selectionChanged)
            {
                Invalidate();
            }
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo mostrar el overlay de layout.", exception);
            Hide();
        }
    }

    public void UpdateSelectedZone(int? zoneNumber)
    {
        if (!isVisible)
        {
            return;
        }

        if (selectedZone != zoneNumber)
        {
            selectedZone = zoneNumber;
            Invalidate();
        }
    }

    public void Hide()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
        }

        isVisible = false;
        selectedZone = null;
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        try
        {
            Hide();

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
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo liberar completamente el overlay de layout.", exception);
        }
        finally
        {
            isDisposed = true;
            GC.SuppressFinalize(this);
        }
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
            throw new InvalidOperationException($"No se pudo registrar la clase del overlay. Código Win32: {Marshal.GetLastWin32Error()}.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT,
            windowClassName,
            "MinimalShell Layout Overlay",
            WS_POPUP,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
            throw new InvalidOperationException($"No se pudo crear el overlay. Código Win32: {Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return message switch
            {
                NativeMethods.WM_PAINT => Paint(hWnd),
                NativeMethods.WM_ERASEBKGND => new IntPtr(1),
                NativeMethods.WM_NCHITTEST => new IntPtr(NativeMethods.HTTRANSPARENT),
                NativeMethods.WM_MOUSEACTIVATE => new IntPtr(NativeMethods.MA_NOACTIVATE),
                NativeMethods.WM_CLOSE => HideAndReturn(),
                _ => NativeMethods.DefWindowProc(hWnd, message, wParam, lParam)
            };
        }
        catch (Exception exception)
        {
            logger.Error("Error procesando el overlay de layout.", exception);
            return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    private IntPtr Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        if (deviceContext == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var background = NativeMethods.CreateSolidBrush(0x00303030);
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
            var fontHeight = Math.Max(
                12,
                (int)Math.Round(
                    Math.Min(workArea.Width, workArea.Height) * zoneNumberSizePercent / 100.0,
                    MidpointRounding.AwayFromZero));
            var font = NativeMethods.CreateFont(
                -fontHeight,
                0,
                0,
                0,
                700,
                0,
                0,
                0,
                NativeMethods.DEFAULT_CHARSET,
                NativeMethods.OUT_DEFAULT_PRECIS,
                NativeMethods.CLIP_DEFAULT_PRECIS,
                NativeMethods.CLEARTYPE_QUALITY,
                NativeMethods.DEFAULT_PITCH | NativeMethods.FF_DONTCARE,
                "Segoe UI");
            var previousFont = font == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.SelectObject(deviceContext, font);

            try
            {
                foreach (var zone in zones)
                {
                    var absolute = LayoutZoneCalculator.ToWindowRect(zone, workArea);
                    var rectangle = new NativeMethods.Rect
                    {
                        Left = absolute.Left - workArea.Left,
                        Top = absolute.Top - workArea.Top,
                        Right = absolute.Right - workArea.Left,
                        Bottom = absolute.Bottom - workArea.Top
                    };
                    var color = selectedZone == zone.Number ? 0x00C08040u : 0x00606060u;
                    var brush = NativeMethods.CreateSolidBrush(color);
                    try
                    {
                        NativeMethods.FillRect(deviceContext, ref rectangle, brush);
                    }
                    finally
                    {
                        NativeMethods.DeleteObject(brush);
                    }

                    var label = zone.Number.ToString();
                    NativeMethods.DrawText(
                        deviceContext,
                        label,
                        label.Length,
                        ref rectangle,
                        NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                }
            }
            finally
            {
                if (font != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(deviceContext, previousFont);
                    NativeMethods.DeleteObject(font);
                }
            }
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paintStruct);
        }

        return IntPtr.Zero;
    }

    private IntPtr HideAndReturn()
    {
        Hide();
        return IntPtr.Zero;
    }

    private void Invalidate()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, false);
        }
    }
}
