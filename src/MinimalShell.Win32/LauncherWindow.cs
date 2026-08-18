using System.Runtime.InteropServices;
using MinimalShell.Core.Applications;

namespace MinimalShell.Win32;

public sealed class LauncherWindow : IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint VK_SPACE = 0x20;

    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"MinimalShell.LauncherWindow.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly IApplicationCatalog catalog;
    private readonly int windowWidth = 680;
    private readonly int windowHeight = 420;
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private string query = string.Empty;
    private IReadOnlyList<ApplicationEntry> results = Array.Empty<ApplicationEntry>();
    private int selectedIndex;
    private bool isVisible;
    private bool isDisposed;

    public LauncherWindow(IApplicationCatalog catalog)
    {
        this.catalog = catalog;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public event Action<ApplicationEntry>? ApplicationSelected;

    public event Action<string>? CommandRequested;

    public event Action? Closed;

    public bool IsVisible => isVisible;

    public void Toggle()
    {
        if (isVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        EnsureWindow();
        query = string.Empty;
        selectedIndex = 0;
        UpdateResults();

        var screenWidth = NativeMethods.GetSystemMetrics(0);
        var screenHeight = NativeMethods.GetSystemMetrics(1);
        var x = Math.Max(0, (screenWidth - windowWidth) / 2);
        var y = Math.Max(0, (screenHeight - windowHeight) / 3);

        NativeMethods.SetWindowPos(
            windowHandle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            windowWidth,
            windowHeight,
            SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(windowHandle);
        NativeMethods.SetFocus(windowHandle);
        isVisible = true;
        NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
    }

    public void Hide()
    {
        if (!isVisible || windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
        isVisible = false;
        Closed?.Invoke();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

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
            throw new InvalidOperationException("No se pudo registrar la ventana del launcher.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            windowClassName,
            "MinimalShell Launcher",
            WS_POPUP | WS_BORDER,
            0,
            0,
            windowWidth,
            windowHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
            throw new InvalidOperationException("No se pudo crear la ventana del launcher.");
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_CHAR:
                HandleCharacter((char)wParam.ToInt32());
                return IntPtr.Zero;

            case NativeMethods.WM_KEYDOWN:
                HandleKey(wParam.ToInt32());
                return IntPtr.Zero;

            case NativeMethods.WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_CLOSE:
                Hide();
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    private void HandleCharacter(char character)
    {
        if (!isVisible || char.IsControl(character))
        {
            return;
        }

        query += character;
        selectedIndex = 0;
        UpdateResults();
    }

    private void HandleKey(int virtualKey)
    {
        if (!isVisible)
        {
            return;
        }

        switch (virtualKey)
        {
            case NativeMethods.VK_BACK:
                if (query.Length > 0)
                {
                    query = query[..^1];
                    selectedIndex = 0;
                    UpdateResults();
                }

                break;

            case NativeMethods.VK_ESCAPE:
                Hide();
                break;

            case NativeMethods.VK_UP:
                if (results.Count > 0)
                {
                    selectedIndex = Math.Max(0, selectedIndex - 1);
                    Invalidate();
                }

                break;

            case NativeMethods.VK_DOWN:
                if (results.Count > 0)
                {
                    selectedIndex = Math.Min(results.Count - 1, selectedIndex + 1);
                    Invalidate();
                }

                break;

            case NativeMethods.VK_RETURN:
                Confirm();
                break;
        }
    }

    private void Confirm()
    {
        if (query.StartsWith('!'))
        {
            var command = query[1..].Trim();

            if (command.Length > 0)
            {
                Hide();
                CommandRequested?.Invoke(command);
            }

            return;
        }

        if (results.Count == 0)
        {
            return;
        }

        var application = results[selectedIndex];
        Hide();
        ApplicationSelected?.Invoke(application);
    }

    private void UpdateResults()
    {
        results = query.StartsWith('!')
            ? Array.Empty<ApplicationEntry>()
            : catalog.Search(query).Take(8).ToArray();
        selectedIndex = results.Count == 0 ? 0 : Math.Min(selectedIndex, results.Count - 1);
        Invalidate();
    }

    private void Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        var background = NativeMethods.CreateSolidBrush(0x00252525);
        var rectangle = new NativeMethods.Rect { Left = 0, Top = 0, Right = windowWidth, Bottom = windowHeight };
        NativeMethods.FillRect(deviceContext, ref rectangle, background);
        NativeMethods.DeleteObject(background);
        NativeMethods.SetBkMode(deviceContext, (int)NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(deviceContext, 0x00FFFFFF);

        var prompt = query.StartsWith('!')
            ? $"Ejecutar: {query[1..]}"
            : query.Length == 0 ? "Buscar aplicación..." : query;
        NativeMethods.TextOut(deviceContext, 24, 24, prompt, prompt.Length);

        var y = 72;
        if (query.StartsWith('!'))
        {
            NativeMethods.SetTextColor(deviceContext, 0x00A0A0A0);
            const string instruction = "Enter para confirmar · Escape para cancelar";
            NativeMethods.TextOut(deviceContext, 24, y, instruction, instruction.Length);
        }
        else
        {
            foreach (var (application, index) in results.Select((application, index) => (application, index)))
            {
                NativeMethods.SetTextColor(deviceContext, (uint)(index == selectedIndex ? 0x0000D7FF : 0x00FFFFFF));
                var line = $"{(index == selectedIndex ? "> " : "  ")}{application.DisplayName}";
                NativeMethods.TextOut(deviceContext, 24, y, line, line.Length);
                y += 34;
            }
        }

        NativeMethods.EndPaint(hWnd, ref paintStruct);
    }

    private void Invalidate()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
        }
    }
}
