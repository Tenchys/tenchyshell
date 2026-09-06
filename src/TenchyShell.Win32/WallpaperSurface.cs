using System.Diagnostics;
using System.Runtime.InteropServices;
using TenchyShell.Core.Wallpaper;

namespace TenchyShell.Win32;

/// <summary>Control hijo del Desktop que pinta un bitmap ya preparado, sin depender de Explorer.</summary>
internal sealed class WallpaperSurface : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_CHILD = 0x40000000;
    private const nuint ForegroundTimerId = 1;
    private const uint ForegroundDelayMilliseconds = 100;
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly NativeMethods.WinEventProc foregroundChangedProcedure;
    private readonly string className = $"TenchyShell.Wallpaper.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private IntPtr windowHandle, bitmap, foregroundEventHook;
    private ushort classAtom;
    private int bitmapWidth, bitmapHeight;
    private bool disposed;

    public WallpaperSurface()
    {
        windowProcedure = WindowProcedure;
        foregroundChangedProcedure = OnForegroundChanged;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public bool SetImage(string path, out WallpaperPreparationTelemetry telemetry, out string? error)
    {
        telemetry = default;
        try
        {
            EnsureWindow();
            var bounds = GetVirtualDesktopBounds();
            var newBitmap = PrepareBitmap(path, bounds.Width, bounds.Height, out telemetry);
            if (newBitmap == IntPtr.Zero) { error = "Windows no pudo decodificar o preparar la imagen seleccionada."; return false; }
            ReplaceBitmap(newBitmap, bounds.Width, bounds.Height);
            ShowOnDesktop(bounds);
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
            error = null;
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    public bool SetNoBackground(out string? error)
    {
        try
        {
            EnsureWindow();
            var bounds = GetVirtualDesktopBounds();
            ReplaceBitmap(IntPtr.Zero, 0, 0);
            ShowOnDesktop(bounds);
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
            error = null;
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (foregroundEventHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(foregroundEventHook);
        if (windowHandle != IntPtr.Zero) { NativeMethods.KillTimer(windowHandle, ForegroundTimerId); NativeMethods.DestroyWindow(windowHandle); }
        if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
        if (classAtom != 0) NativeMethods.UnregisterClass(className, moduleHandle);
    }

    private void ReplaceBitmap(IntPtr newBitmap, int width, int height)
    {
        var oldBitmap = bitmap;
        bitmap = newBitmap; bitmapWidth = width; bitmapHeight = height;
        if (oldBitmap != IntPtr.Zero) NativeMethods.DeleteObject(oldBitmap);
    }

    private void EnsureWindow()
    {
        if (windowHandle != IntPtr.Zero) return;
        var windowClass = new NativeMethods.WindowClass { Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), WindowProcedure = windowProcedure, Instance = moduleHandle, ClassName = className };
        classAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (classAtom == 0) throw new InvalidOperationException("No se pudo registrar el control de wallpaper.");
        windowHandle = NativeMethods.CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, className, "TenchyShell Wallpaper", WS_CHILD, 0, 0, 1, 1, NativeMethods.GetDesktopWindow(), IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero) throw new InvalidOperationException("No se pudo crear el control de wallpaper.");
        foregroundEventHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundChangedProcedure, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void ShowOnDesktop(VirtualDesktopBounds bounds) => NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_BOTTOM, bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_PAINT)
        {
            var target = NativeMethods.BeginPaint(hWnd, out var paint);
            var rectangle = paint.PaintRectangle;
            var brush = NativeMethods.CreateSolidBrush(0x00000000);
            NativeMethods.FillRect(target, ref rectangle, brush);
            NativeMethods.DeleteObject(brush);
            if (bitmap != IntPtr.Zero && bitmapWidth > 0 && bitmapHeight > 0)
            {
                var source = NativeMethods.CreateCompatibleDC(target);
                var previous = NativeMethods.SelectObject(source, bitmap);
                NativeMethods.BitBlt(target, 0, 0, bitmapWidth, bitmapHeight, source, 0, 0, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(source, previous);
                NativeMethods.DeleteDC(source);
            }
            NativeMethods.EndPaint(hWnd, ref paint);
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_TIMER && (nuint)wParam == ForegroundTimerId)
        {
            NativeMethods.KillTimer(windowHandle, ForegroundTimerId);
            NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
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

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (!disposed && windowHandle != IntPtr.Zero) NativeMethods.PostMessage(windowHandle, NativeMethods.WM_APP_WALLPAPER_FOREGROUND, IntPtr.Zero, IntPtr.Zero);
    }

    private static VirtualDesktopBounds GetVirtualDesktopBounds() => new(NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77), NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));

    private static IntPtr PrepareBitmap(string path, int targetWidth, int targetHeight, out WallpaperPreparationTelemetry telemetry)
    {
        telemetry = default;
        var stopwatch = Stopwatch.StartNew();
        var startup = new GdiplusStartupInput { Version = 1 };
        if (GdiplusStartup(out var token, ref startup, IntPtr.Zero) != 0) return IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromFile(path, out var image) != 0 || image == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                if (GdipGetImageWidth(image, out var sourceWidth) != 0 || GdipGetImageHeight(image, out var sourceHeight) != 0) return IntPtr.Zero;
                if (GdipCreateHBITMAPFromBitmap(image, out var sourceBitmap, 0) != 0 || sourceBitmap == IntPtr.Zero) return IntPtr.Zero;
                try
                {
                    var screen = NativeMethods.GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero) return IntPtr.Zero;
                    try
                    {
                        var targetBitmap = NativeMethods.CreateCompatibleBitmap(screen, targetWidth, targetHeight);
                        var destinationDc = NativeMethods.CreateCompatibleDC(screen);
                        var sourceDc = NativeMethods.CreateCompatibleDC(screen);
                        if (targetBitmap == IntPtr.Zero || destinationDc == IntPtr.Zero || sourceDc == IntPtr.Zero)
                        {
                            if (targetBitmap != IntPtr.Zero) NativeMethods.DeleteObject(targetBitmap);
                            if (destinationDc != IntPtr.Zero) NativeMethods.DeleteDC(destinationDc);
                            if (sourceDc != IntPtr.Zero) NativeMethods.DeleteDC(sourceDc);
                            return IntPtr.Zero;
                        }
                        try
                        {
                            var oldTarget = NativeMethods.SelectObject(destinationDc, targetBitmap);
                            var oldSource = NativeMethods.SelectObject(sourceDc, sourceBitmap);
                            var prepared = false;
                            try
                            {
                                var rectangle = new NativeMethods.Rect { Left = 0, Top = 0, Right = targetWidth, Bottom = targetHeight };
                                var brush = NativeMethods.CreateSolidBrush(0x00000000);
                                NativeMethods.FillRect(destinationDc, ref rectangle, brush);
                                NativeMethods.DeleteObject(brush);
                                var layout = WallpaperImageLayout.CalculateCover((int)sourceWidth, (int)sourceHeight, targetWidth, targetHeight);
                                if (!NativeMethods.StretchBlt(destinationDc, layout.X, layout.Y, layout.Width, layout.Height, sourceDc, 0, 0, (int)sourceWidth, (int)sourceHeight, NativeMethods.SRCCOPY)) return IntPtr.Zero;
                                stopwatch.Stop();
                                telemetry = new((int)sourceWidth, (int)sourceHeight, targetWidth, targetHeight, stopwatch.ElapsedMilliseconds, checked((long)targetWidth * targetHeight * 4));
                                prepared = true;
                                return targetBitmap;
                            }
                            finally
                            {
                                NativeMethods.SelectObject(destinationDc, oldTarget);
                                NativeMethods.SelectObject(sourceDc, oldSource);
                                if (!prepared) NativeMethods.DeleteObject(targetBitmap);
                            }
                        }
                        finally { NativeMethods.DeleteDC(destinationDc); NativeMethods.DeleteDC(sourceDc); }
                    }
                    finally { NativeMethods.ReleaseDC(IntPtr.Zero, screen); }
                }
                finally { NativeMethods.DeleteObject(sourceBitmap); }
            }
            finally { GdipDisposeImage(image); }
        }
        finally { GdiplusShutdown(token); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct GdiplusStartupInput { internal uint Version; internal IntPtr DebugEventCallback; internal int SuppressBackgroundThread; internal int SuppressExternalCodecs; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out ulong token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(ulong token);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateHBITMAPFromBitmap(IntPtr bitmap, out IntPtr hBitmap, int background);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageWidth(IntPtr image, out uint width);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageHeight(IntPtr image, out uint height);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(IntPtr image);
    private readonly record struct VirtualDesktopBounds(int X, int Y, int Width, int Height);
}

internal readonly record struct WallpaperPreparationTelemetry(int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight, long DurationMilliseconds, long EstimatedBytes);
