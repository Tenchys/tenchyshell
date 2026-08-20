using System.Runtime.InteropServices;

namespace TenchyShell.Win32;

/// <summary>Control hijo del Desktop que pinta el fondo sin depender de Explorer.</summary>
internal sealed class WallpaperSurface : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_CHILD = 0x40000000;
    private const nuint ForegroundTimerId = 1;
    private const uint ForegroundDelayMilliseconds = 100;

    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly NativeMethods.WinEventProc foregroundChangedProcedure;
    private readonly string className = $"TenchyShell.Wallpaper.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private IntPtr windowHandle;
    private ushort classAtom;
    private IntPtr bitmap;
    private IntPtr foregroundEventHook;
    private int bitmapWidth;
    private int bitmapHeight;
    private bool disposed;

    public WallpaperSurface()
    {
        windowProcedure = WindowProcedure;
        foregroundChangedProcedure = OnForegroundChanged;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public bool SetImage(string path, out string? error)
    {
        try
        {
            EnsureWindow();
            var newBitmap = LoadBitmap(path, out var width, out var height);
            if (newBitmap == IntPtr.Zero)
            {
                error = "Windows no pudo decodificar la imagen seleccionada.";
                return false;
            }

            var oldBitmap = bitmap;
            bitmap = newBitmap;
            bitmapWidth = width;
            bitmapHeight = height;
            if (oldBitmap != IntPtr.Zero) NativeMethods.DeleteObject(oldBitmap);
            ShowOnDesktop();
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (foregroundEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(foregroundEventHook);
            foregroundEventHook = IntPtr.Zero;
        }

        if (windowHandle != IntPtr.Zero) NativeMethods.KillTimer(windowHandle, ForegroundTimerId);
        if (windowHandle != IntPtr.Zero) NativeMethods.DestroyWindow(windowHandle);
        if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
        if (classAtom != 0) NativeMethods.UnregisterClass(className, moduleHandle);
    }

    private void EnsureWindow()
    {
        if (windowHandle != IntPtr.Zero) return;
        var windowClass = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            WindowProcedure = windowProcedure,
            Instance = moduleHandle,
            ClassName = className
        };
        classAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (classAtom == 0) throw new InvalidOperationException("No se pudo registrar el control de wallpaper.");

        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            className,
            "TenchyShell Wallpaper",
            WS_CHILD,
            0, 0, 1, 1,
            NativeMethods.GetDesktopWindow(), IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero) throw new InvalidOperationException("No se pudo crear el control de wallpaper.");

        foregroundEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            foregroundChangedProcedure,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void ShowOnDesktop()
    {
        var width = NativeMethods.GetSystemMetrics(78);
        var height = NativeMethods.GetSystemMetrics(79);
        NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_BOTTOM, 0, 0, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_PAINT)
        {
            var target = NativeMethods.BeginPaint(hWnd, out var paintStruct);
            if (bitmap != IntPtr.Zero && bitmapWidth > 0 && bitmapHeight > 0)
            {
                var source = NativeMethods.CreateCompatibleDC(target);
                var previous = NativeMethods.SelectObject(source, bitmap);
                var width = NativeMethods.GetSystemMetrics(78);
                var height = NativeMethods.GetSystemMetrics(79);
                NativeMethods.StretchBlt(target, 0, 0, width, height, source, 0, 0, bitmapWidth, bitmapHeight, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(source, previous);
                NativeMethods.DeleteDC(source);
            }
            NativeMethods.EndPaint(hWnd, ref paintStruct);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_TIMER && (nuint)wParam == ForegroundTimerId)
        {
            NativeMethods.KillTimer(windowHandle, ForegroundTimerId);
            NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_APP_WALLPAPER_FOREGROUND)
        {
            NativeMethods.KillTimer(windowHandle, ForegroundTimerId);
            NativeMethods.SetTimer(windowHandle, ForegroundTimerId, ForegroundDelayMilliseconds, IntPtr.Zero);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_NCHITTEST) return new IntPtr(NativeMethods.HTTRANSPARENT);
        if (message == NativeMethods.WM_MOUSEACTIVATE) return new IntPtr(NativeMethods.MA_NOACTIVATE);
        if (message == NativeMethods.WM_ERASEBKGND) return new IntPtr(1);
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void OnForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        var handle = windowHandle;
        if (disposed || handle == IntPtr.Zero) return;
        NativeMethods.PostMessage(handle, NativeMethods.WM_APP_WALLPAPER_FOREGROUND, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr LoadBitmap(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        var startup = new GdiplusStartupInput { Version = 1 };
        if (GdiplusStartup(out var token, ref startup, IntPtr.Zero) != 0) return IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromFile(path, out var image) != 0 || image == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                if (GdipGetImageWidth(image, out var sourceWidth) != 0 || GdipGetImageHeight(image, out var sourceHeight) != 0) return IntPtr.Zero;
                width = (int)sourceWidth;
                height = (int)sourceHeight;
                return GdipCreateHBITMAPFromBitmap(image, out var handle, 0) == 0 ? handle : IntPtr.Zero;
            }
            finally
            {
                GdipDisposeImage(image);
            }
        }
        finally
        {
            GdiplusShutdown(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        internal uint Version;
        internal IntPtr DebugEventCallback;
        internal int SuppressBackgroundThread;
        internal int SuppressExternalCodecs;
    }

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out ulong token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(ulong token);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);
    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateHBITMAPFromBitmap(IntPtr bitmap, out IntPtr hBitmap, int background);
    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);
}
