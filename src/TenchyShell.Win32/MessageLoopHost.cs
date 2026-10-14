using System.ComponentModel;
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
    private readonly object postedActionsGate = new();
    private readonly LinkedList<Action> postedActions = new();
    private IntPtr windowHandle;
    private ushort windowClassAtom;
    private int messageLoopManagedThreadId;
    private bool isRunning;
    private bool isDisposed;
    private bool acceptsConfiguration;
    private int pendingActionCount;
    private int maximumPendingActionCount;

    public MessageLoopHost()
    {
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public event Action<Exception>? FatalError;

    public event Action<int>? HotkeyPressed;

    public event Action<int>? HotkeyRegistered;

    public event Action<int, int>? HotkeyRegistrationFailed;

    /// <summary>Indica si el código se está ejecutando en el hilo que posee la cola Win32.</summary>
    public bool IsOnMessageLoopThread => isRunning && messageLoopManagedThreadId == Environment.CurrentManagedThreadId;

    /// <summary>Trabajo pendiente observado sin sondeo; útil para telemetría opt-in.</summary>
    public int PendingActionCount => Volatile.Read(ref pendingActionCount);

    /// <summary>Máxima profundidad observada desde la creación del host.</summary>
    public int MaximumPendingActionCount => Volatile.Read(ref maximumPendingActionCount);

    /// <summary>Encola trabajo en el hilo que posee la cola Win32.</summary>
    public void Post(Action action) => TryPost(action);

    /// <summary>Intenta encolar trabajo y permite al llamador registrar un fallo observable.</summary>
    public bool TryPost(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (postedActionsGate)
        {
            if (isDisposed || windowHandle == IntPtr.Zero) return false;

            var entry = postedActions.AddLast(action);
            var count = ++pendingActionCount;
            UpdateMaximumPendingActions(count);
            if (NativeMethods.PostMessage(windowHandle, NativeMethods.WM_APP_EXECUTE, IntPtr.Zero, IntPtr.Zero)) return true;

            postedActions.Remove(entry);
            --pendingActionCount;
            return false;
        }
    }

    public void ConfigureHotkey(int id, HotkeyCombination combination, bool required = false)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (isRunning && !acceptsConfiguration)
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

    public int Run(
        Action? onInitialized = null,
        Action? onHotkeysReady = null)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        if (isRunning)
        {
            throw new InvalidOperationException("El message loop ya está ejecutándose.");
        }

        CreateMessageWindow();
        isRunning = true;
        messageLoopManagedThreadId = Environment.CurrentManagedThreadId;
        acceptsConfiguration = true;

        try
        {
            onInitialized?.Invoke();
            acceptsConfiguration = false;
            RegisterConfiguredHotkeys(requiredOnly: true);
            RegisterConfiguredHotkeys(requiredOnly: false);
            onHotkeysReady?.Invoke();

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
            acceptsConfiguration = false;
            isRunning = false;
            messageLoopManagedThreadId = 0;
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
            while (TryTakePostedAction(out var action))
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
        lock (postedActionsGate)
        {
            postedActions.Clear();
            Volatile.Write(ref pendingActionCount, 0);
        }

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

    private bool TryTakePostedAction(out Action action)
    {
        lock (postedActionsGate)
        {
            if (postedActions.First is null)
            {
                action = null!;
                return false;
            }

            action = postedActions.First.Value;
            postedActions.RemoveFirst();
            --pendingActionCount;
            return true;
        }
    }

    private void UpdateMaximumPendingActions(int count)
    {
        while (true)
        {
            var previous = Volatile.Read(ref maximumPendingActionCount);
            if (count <= previous || Interlocked.CompareExchange(ref maximumPendingActionCount, count, previous) == previous) return;
        }
    }
}
