using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TenchyShell.Win32;

public sealed class MessageLoopHost : IDisposable
{
    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"TenchyShell.MessageWindow.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly Dictionary<int, HotkeyCombination> hotkeys = new();
    private readonly HashSet<int> requiredHotkeys = new();
    private readonly HashSet<int> registeredHotkeys = new();
    private readonly ConcurrentQueue<Action> postedActions = new();
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private bool isRunning;
    private bool isDisposed;

    public MessageLoopHost()
    {
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public event Action<Exception>? FatalError;

    public event Action<int>? HotkeyPressed;

    public event Action<int>? HotkeyRegistered;

    public event Action<int, int>? HotkeyRegistrationFailed;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (isDisposed || windowHandle == IntPtr.Zero)
        {
            return;
        }

        postedActions.Enqueue(action);
        NativeMethods.PostMessage(windowHandle, NativeMethods.WM_APP_EXECUTE, IntPtr.Zero, IntPtr.Zero);
    }

    public void ConfigureHotkey(int id, HotkeyCombination combination, bool required = false)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (isRunning)
        {
            throw new InvalidOperationException("No se pueden configurar hotkeys mientras el message loop está ejecutándose.");
        }

        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "El identificador del hotkey debe ser positivo.");
        }

        if (hotkeys.Values.Any(existing => existing == combination))
        {
            throw new InvalidOperationException("No se puede registrar la misma combinación para dos acciones.");
        }

        hotkeys[id] = combination;
        if (required)
        {
            requiredHotkeys.Add(id);
        }
    }

    public int Run(Action? onInitialized = null)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (isRunning)
        {
            throw new InvalidOperationException("El message loop ya está ejecutándose.");
        }

        CreateMessageWindow();
        isRunning = true;

        try
        {
            onInitialized?.Invoke();
            RegisterConfiguredHotkeys(requiredOnly: false);

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage falló.");
                }

                if (result == 0)
                {
                    return 0;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            isRunning = false;
            ReleaseNativeResources();
        }
    }

    public void Stop()
    {
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.PostMessage(windowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        Stop();

        if (!isRunning)
        {
            ReleaseNativeResources();
        }

        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void CreateMessageWindow()
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo registrar la clase de ventana del shell.");
        }

        windowHandle = NativeMethods.CreateWindowEx(
            0,
            windowClassName,
            "TenchyShell",
            0,
            0,
            0,
            0,
            0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (windowHandle == IntPtr.Zero)
        {
            ReleaseNativeResources();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear la ventana de mensajes del shell.");
        }

        RegisterConfiguredHotkeys(requiredOnly: true);
    }

    private void RegisterConfiguredHotkeys(bool requiredOnly)
    {
        foreach (var registration in hotkeys)
        {
            if (registeredHotkeys.Contains(registration.Key) || requiredHotkeys.Contains(registration.Key) != requiredOnly)
            {
                continue;
            }

            if (!NativeMethods.RegisterHotKey(
                    windowHandle,
                    registration.Key,
                    registration.Value.Modifiers,
                    registration.Value.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();

                if (requiredOnly)
                {
                    ReleaseNativeResources();
                    throw new Win32Exception(error, $"No se pudo registrar el hotkey con identificador {registration.Key}.");
                }

                HotkeyRegistrationFailed?.Invoke(registration.Key, error);
                continue;
            }

            registeredHotkeys.Add(registration.Key);
            HotkeyRegistered?.Invoke(registration.Key);
        }
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_APP_EXECUTE)
        {
            while (postedActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    FatalError?.Invoke(exception);
                    Stop();
                    break;
                }
            }

            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_HOTKEY)
        {
            try
            {
                HotkeyPressed?.Invoke(wParam.ToInt32());
            }
            catch (Exception exception)
            {
                FatalError?.Invoke(exception);
                Stop();
            }

            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_CLOSE)
        {
            NativeMethods.DestroyWindow(hWnd);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ReleaseNativeResources()
    {
        if (windowHandle != IntPtr.Zero)
        {
            foreach (var hotkeyId in registeredHotkeys)
            {
                NativeMethods.UnregisterHotKey(windowHandle, hotkeyId);
            }

            registeredHotkeys.Clear();
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }

        if (windowClassAtom != 0)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
        }
    }
}
