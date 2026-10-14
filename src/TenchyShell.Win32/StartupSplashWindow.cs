using System.Runtime.InteropServices;
using TenchyShell.Core;
using TenchyShell.Core.Startup;

namespace TenchyShell.Win32;

/// <summary>
/// Superficie temporal de arranque dibujada con Win32/GDI. No crea un loop
/// propio: el hilo que posee MessageLoopHost procesa sus mensajes.
/// </summary>
public sealed class StartupSplashWindow : IDisposable
{
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopmost = 0x00000008;
    private const int Width = 760;
    private const int Height = 520;
    private const int ReferenceImageSize = 245;
    // El icono ocupa el panel izquierdo y deja una columna amplia y limpia
    // para identidad y estado en la derecha.
    private const int ReferenceImageX = 60;
    private const int ReferenceImageY = 137;
    private const int ReferenceArtworkInset = 18;
    private const int ReferenceArtworkSize = 209;
    private const int ReferenceArtworkCornerDiameter = 62;
    private const int TextColumnX = 330;
    private const int FontWeightNormal = 400;
    private const int FontWeightBold = 700;

    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"TenchyShell.StartupSplash.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly IStartupProgressReporter progress;
    private readonly bool keepVisibleWhenReady;
    private readonly string referenceImagePath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "TenchyShellSplash.png");
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private IntPtr referenceImage;
    private nuint gdiplusToken;
    private string statusMessage = "Preparando TenchyShell…";
    private StartupModule? currentModule;
    private int activityTick;
    private bool isDisposed;
    private bool sessionReady;

    public StartupSplashWindow(IStartupProgressReporter progress, bool keepVisibleWhenReady = false)
    {
        this.progress = progress ?? throw new ArgumentNullException(nameof(progress));
        this.keepVisibleWhenReady = keepVisibleWhenReady;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
        progress.Changed += OnProgressChanged;

        try
        {
            CreateWindow();
            LoadReferenceImage();
            Show();
            Apply(progress.Snapshot);
        }
        catch
        {
            progress.Changed -= OnProgressChanged;
            DestroyNativeResources();
            throw;
        }
    }

    public bool IsVisible { get; private set; }

    public IntPtr WindowHandle => windowHandle;

    public void Close()
    {
        if (isDisposed) return;

        IsVisible = false;
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }

        if (windowClassAtom != 0)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
        }

        ReleaseReferenceImage();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        progress.Changed -= OnProgressChanged;
        Close();
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void CreateWindow()
    {
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
            throw new InvalidOperationException("No se pudo registrar la clase de ventana del splash de inicio.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            WsExToolWindow | WsExTopmost | NativeMethods.WS_EX_NOACTIVATE,
            windowClassName,
            "TenchyShell",
            NativeMethods.WS_POPUP,
            0,
            0,
            Width,
            Height,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
            throw new InvalidOperationException($"No se pudo crear la ventana del splash de inicio. Código Win32: {error}.");
        }
    }

    private void Show()
    {
        var screenWidth = NativeMethods.GetSystemMetrics(0);
        var screenHeight = NativeMethods.GetSystemMetrics(1);
        var x = Math.Max(0, (screenWidth - Width) / 2);
        var y = Math.Max(0, (screenHeight - Height) / 2);
        if (!NativeMethods.SetWindowPos(
                windowHandle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW))
        {
            throw new InvalidOperationException("No se pudo posicionar la ventana del splash de inicio.");
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOWNOACTIVATE);
        IsVisible = true;
        NativeMethods.UpdateWindow(windowHandle);
    }

    private void OnProgressChanged(StartupProgressSnapshot snapshot)
    {
        if (isDisposed) return;
        sessionReady = snapshot.IsReady;
        Apply(snapshot);
        if (snapshot.IsFailed || (snapshot.IsReady && !keepVisibleWhenReady))
        {
            // La ventana desaparece antes de que el shell entre en su ciclo
            // normal; Dispose se ejecuta después desde Program.
            Close();
        }
    }

    private void Apply(StartupProgressSnapshot snapshot)
    {
        currentModule = snapshot.CurrentModule;
        statusMessage = snapshot.IsFailed
            ? $"Error: {snapshot.ErrorMessage ?? snapshot.Message}"
            : snapshot.IsReady && keepVisibleWhenReady
                ? "Listo · clic para cerrar"
                : snapshot.Message;
        activityTick++;
        InvalidateAndPaint();
    }

    private void InvalidateAndPaint()
    {
        if (windowHandle == IntPtr.Zero) return;
        NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
        // UpdateWindow fuerza el WM_PAINT en el mismo hilo. Así el splash
        // sigue siendo visible durante las fases síncronas previas al loop.
        NativeMethods.UpdateWindow(windowHandle);
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        return message switch
        {
            NativeMethods.WM_PAINT => Paint(hWnd),
            NativeMethods.WM_ERASEBKGND => new IntPtr(1),
            NativeMethods.WM_NCHITTEST => new IntPtr(keepVisibleWhenReady && sessionReady
                ? NativeMethods.HTCLIENT
                : NativeMethods.HTTRANSPARENT),
            NativeMethods.WM_MOUSEACTIVATE => new IntPtr(NativeMethods.MA_NOACTIVATE),
            NativeMethods.WM_LBUTTONDOWN when keepVisibleWhenReady && sessionReady => CloseFromWindowProcedure(),
            NativeMethods.WM_CLOSE => CloseFromWindowProcedure(),
            _ => NativeMethods.DefWindowProc(hWnd, message, wParam, lParam)
        };
    }

    private IntPtr CloseFromWindowProcedure()
    {
        Close();
        return IntPtr.Zero;
    }

    private IntPtr Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        try
        {
            var background = NativeMethods.CreateSolidBrush(Rgb(10, 18, 29));
            var client = new NativeMethods.Rect { Right = Width, Bottom = Height };
            NativeMethods.FillRect(deviceContext, ref client, background);
            NativeMethods.DeleteObject(background);

            if (referenceImage == IntPtr.Zero)
            {
                DrawArtwork(deviceContext);
            }
            else
            {
                DrawReferenceImage(deviceContext);
            }
            DrawText(deviceContext, ProductInfo.Name, TextColumnX, 112, 36, FontWeightBold, Rgb(241, 245, 249));
            DrawText(deviceContext, $"v{ProductInfo.Version}", TextColumnX + 2, 160, 14, FontWeightBold, Rgb(29, 225, 218));
            DrawText(deviceContext, "sesión ligera · orientada al teclado", TextColumnX + 2, 188, 14, FontWeightNormal, Rgb(148, 163, 184));
            DrawText(deviceContext, statusMessage, TextColumnX, 330, 15, FontWeightNormal, Rgb(226, 232, 240));
            DrawText(deviceContext, currentModule is null
                ? sessionReady ? "LISTO" : "INICIO"
                : $"MÓDULO {(int)currentModule}: {currentModule}", TextColumnX, 364, 11, FontWeightBold, Rgb(29, 225, 218));
            DrawActivity(deviceContext);
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, ref paintStruct);
        }

        return IntPtr.Zero;
    }

    private void LoadReferenceImage()
    {
        if (!File.Exists(referenceImagePath)) return;

        var startup = new NativeMethods.GdiplusStartupInput { Version = 1 };
        if (NativeMethods.GdiplusStartup(out gdiplusToken, ref startup, out _) != 0)
        {
            gdiplusToken = 0;
            return;
        }

        if (NativeMethods.GdipLoadImageFromFile(referenceImagePath, out referenceImage) != 0)
        {
            referenceImage = IntPtr.Zero;
            NativeMethods.GdiplusShutdown(gdiplusToken);
            gdiplusToken = 0;
        }
    }

    private void DrawReferenceImage(IntPtr deviceContext)
    {
        // El PNG de referencia incluye un lienzo cuadrado oscuro. El clip se
        // ajusta al marco exterior redondeado del icono, por lo que el lienzo
        // no ocupa una segunda superficie dentro del splash.
        var clipRegion = NativeMethods.CreateRoundRectRgn(
            ReferenceImageX + ReferenceArtworkInset,
            ReferenceImageY + ReferenceArtworkInset,
            ReferenceImageX + ReferenceArtworkInset + ReferenceArtworkSize,
            ReferenceImageY + ReferenceArtworkInset + ReferenceArtworkSize,
            ReferenceArtworkCornerDiameter,
            ReferenceArtworkCornerDiameter);
        if (clipRegion == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SelectClipRgn(deviceContext, clipRegion);
        if (NativeMethods.GdipCreateFromHDC(deviceContext, out var graphics) != 0)
        {
            NativeMethods.SelectClipRgn(deviceContext, IntPtr.Zero);
            NativeMethods.DeleteObject(clipRegion);
            return;
        }

        try
        {
            // Se usa la ruta rectangular básica de GDI+, estable en las
            // arquitecturas soportadas, limitada por el clip anterior.
            NativeMethods.GdipDrawImageRectI(
                graphics,
                referenceImage,
                ReferenceImageX,
                ReferenceImageY,
                ReferenceImageSize,
                ReferenceImageSize);
        }
        finally
        {
            NativeMethods.GdipDeleteGraphics(graphics);
            NativeMethods.SelectClipRgn(deviceContext, IntPtr.Zero);
            NativeMethods.DeleteObject(clipRegion);
        }
    }

    private static void DrawArtwork(IntPtr deviceContext)
    {
        // La geometría sigue la referencia: marco cuadrado, cuatro paneles,
        // barra trapezoidal central y prompt de terminal.
        DrawRoundedOutline(deviceContext, 18, 24, 500, 496, 76, Rgb(33, 128, 255), 5);
        DrawRoundedOutline(deviceContext, 20, 26, 498, 494, 74, Rgb(20, 216, 226), 2);

        DrawRoundedOutline(deviceContext, 70, 90, 198, 184, 14, Rgb(29, 225, 218), 6);
        DrawRoundedOutline(deviceContext, 207, 90, 474, 186, 14, Rgb(74, 143, 255), 6);
        DrawRoundedOutline(deviceContext, 70, 193, 198, 294, 14, Rgb(22, 157, 255), 6);
        DrawRoundedOutline(deviceContext, 390, 196, 476, 340, 14, Rgb(151, 91, 247), 6);

        DrawTerminalBar(deviceContext);
        DrawPrompt(deviceContext);
    }

    private void DrawActivity(IntPtr deviceContext)
    {
        for (var index = 0; index < 3; index++)
        {
            var color = index == activityTick % 3 ? Rgb(29, 225, 218) : Rgb(50, 68, 85);
            DrawRectangle(deviceContext, 650 + (index * 18), 454, 8, 8, color);
        }
    }

    private static void DrawRoundedOutline(
        IntPtr deviceContext,
        int left,
        int top,
        int right,
        int bottom,
        int radius,
        uint color,
        int width)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, width, color);
        var previousPen = NativeMethods.SelectObject(deviceContext, pen);
        var previousBrush = NativeMethods.SelectObject(
            deviceContext,
            NativeMethods.GetStockObject(NativeMethods.NULL_BRUSH));
        NativeMethods.RoundRect(deviceContext, left, top, right, bottom, radius, radius);
        NativeMethods.SelectObject(deviceContext, previousBrush);
        NativeMethods.SelectObject(deviceContext, previousPen);
        NativeMethods.DeleteObject(pen);
    }

    private static void DrawTerminalBar(IntPtr deviceContext)
    {
        var points = new[]
        {
            new NativeMethods.Point { X = 112, Y = 252 },
            new NativeMethods.Point { X = 453, Y = 252 },
            new NativeMethods.Point { X = 425, Y = 306 },
            new NativeMethods.Point { X = 94, Y = 306 }
        };
        FillPolygon(deviceContext, points, Rgb(24, 220, 224));

        var violetPoints = new[]
        {
            new NativeMethods.Point { X = 373, Y = 252 },
            new NativeMethods.Point { X = 453, Y = 252 },
            new NativeMethods.Point { X = 425, Y = 306 },
            new NativeMethods.Point { X = 345, Y = 306 }
        };
        FillPolygon(deviceContext, violetPoints, Rgb(182, 112, 247));
    }

    private static void DrawPrompt(IntPtr deviceContext)
    {
        var chevron = new[]
        {
            new NativeMethods.Point { X = 142, Y = 362 },
            new NativeMethods.Point { X = 181, Y = 401 },
            new NativeMethods.Point { X = 142, Y = 440 },
            new NativeMethods.Point { X = 121, Y = 419 },
            new NativeMethods.Point { X = 149, Y = 401 },
            new NativeMethods.Point { X = 121, Y = 383 }
        };
        FillPolygon(deviceContext, chevron, Rgb(72, 229, 210));
        DrawRectangle(deviceContext, 188, 425, 78, 16, Rgb(24, 195, 231));

        DrawRectangle(deviceContext, 270, 330, 48, 112, Rgb(24, 184, 234));
        var violetBlock = new[]
        {
            new NativeMethods.Point { X = 270, Y = 416 },
            new NativeMethods.Point { X = 318, Y = 384 },
            new NativeMethods.Point { X = 318, Y = 442 },
            new NativeMethods.Point { X = 270, Y = 474 }
        };
        FillPolygon(deviceContext, violetBlock, Rgb(182, 112, 247));
    }

    private static void FillPolygon(IntPtr deviceContext, NativeMethods.Point[] points, uint color)
    {
        var brush = NativeMethods.CreateSolidBrush(color);
        var previousBrush = NativeMethods.SelectObject(deviceContext, brush);
        var previousPen = NativeMethods.SelectObject(
            deviceContext,
            NativeMethods.GetStockObject(NativeMethods.NULL_PEN));
        NativeMethods.Polygon(deviceContext, points, points.Length);
        NativeMethods.SelectObject(deviceContext, previousPen);
        NativeMethods.SelectObject(deviceContext, previousBrush);
        NativeMethods.DeleteObject(brush);
    }

    private static void DrawRectangle(IntPtr deviceContext, int x, int y, int width, int height, uint color)
    {
        var brush = NativeMethods.CreateSolidBrush(color);
        var rectangle = new NativeMethods.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        NativeMethods.FillRect(deviceContext, ref rectangle, brush);
        NativeMethods.DeleteObject(brush);
    }

    private static uint Rgb(byte red, byte green, byte blue) =>
        (uint)(red | (green << 8) | (blue << 16));

    private static void DrawText(
        IntPtr deviceContext,
        string text,
        int x,
        int y,
        int size,
        int weight,
        uint color,
        bool center = false)
    {
        var font = NativeMethods.CreateFont(
            -size,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            NativeMethods.DEFAULT_CHARSET,
            NativeMethods.OUT_DEFAULT_PRECIS,
            NativeMethods.CLIP_DEFAULT_PRECIS,
            NativeMethods.CLEARTYPE_QUALITY,
            NativeMethods.DEFAULT_PITCH | NativeMethods.FF_DONTCARE,
            "Segoe UI");
        var previousFont = NativeMethods.SelectObject(deviceContext, font);
        NativeMethods.SetBkMode(deviceContext, (int)NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(deviceContext, color);
        if (center)
        {
            var rectangle = new NativeMethods.Rect { Left = x, Top = y, Right = Width - x, Bottom = y + size + 10 };
            NativeMethods.DrawText(deviceContext, text, text.Length, ref rectangle, NativeMethods.DT_CENTER | NativeMethods.DT_SINGLELINE);
        }
        else
        {
            NativeMethods.TextOut(deviceContext, x, y, text, text.Length);
        }

        NativeMethods.SelectObject(deviceContext, previousFont);
        NativeMethods.DeleteObject(font);
    }

    private void DestroyNativeResources()
    {
        ReleaseReferenceImage();

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

    private void ReleaseReferenceImage()
    {
        if (referenceImage != IntPtr.Zero)
        {
            NativeMethods.GdipDisposeImage(referenceImage);
            referenceImage = IntPtr.Zero;
        }

        if (gdiplusToken != 0)
        {
            NativeMethods.GdiplusShutdown(gdiplusToken);
            gdiplusToken = 0;
        }
    }
}
