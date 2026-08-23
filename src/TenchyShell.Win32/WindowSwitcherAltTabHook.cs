using System.ComponentModel;
using System.Runtime.InteropServices;
using TenchyShell.Core.Logging;

namespace TenchyShell.Win32;

/// <summary>Captura Alt+Tab para el selector del workspace sin crear otro message loop.</summary>
public sealed class WindowSwitcherAltTabHook : IDisposable
{
    private readonly MessageLoopHost messageLoop;
    private readonly WindowSwitcherWindow switcher;
    private readonly ILogger logger;
    private readonly NativeMethods.LowLevelHookProc procedure;
    private readonly IntPtr moduleHandle;
    private IntPtr hookHandle;
    private bool switching;
    private bool disposed;

    public WindowSwitcherAltTabHook(MessageLoopHost messageLoop, WindowSwitcherWindow switcher, ILogger logger)
    {
        this.messageLoop = messageLoop;
        this.switcher = switcher;
        this.logger = logger;
        procedure = OnKeyboardHook;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public void Start()
    {
        if (disposed || hookHandle != IntPtr.Zero) return;
        hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, procedure, moduleHandle, 0);
        if (hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo registrar WH_KEYBOARD_LL para el selector de ventanas.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        if (hookHandle != IntPtr.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(hookHandle))
            {
                logger.Error($"No se pudo liberar el hook Alt+Tab. Código Win32: {Marshal.GetLastWin32Error()}.");
            }
            hookHandle = IntPtr.Zero;
        }
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || disposed) return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardHookData>(lParam);
            var message = unchecked((uint)wParam.ToInt64());
            var down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var up = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

            if (down && data.VirtualKeyCode == NativeMethods.VK_TAB && KeyboardState.IsAltPressed)
            {
                switching = true;
                var backwards = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
                logger.Info($"Selector Alt+Tab iniciado; VK_TAB, inverso={backwards}.");
                messageLoop.Post(() => switcher.BeginAltTab(backwards));
                return new IntPtr(1);
            }

            if (switching && down && data.VirtualKeyCode == NativeMethods.VK_ESCAPE)
            {
                switching = false;
                logger.Info("Selector Alt+Tab cancelado con Escape.");
                messageLoop.Post(switcher.CancelSelection);
                return new IntPtr(1);
            }

            if (switching && down && data.VirtualKeyCode == NativeMethods.VK_RETURN)
            {
                switching = false;
                logger.Info("Selector Alt+Tab confirmado con Enter.");
                messageLoop.Post(switcher.ConfirmSelection);
                return new IntPtr(1);
            }

            if (switching && up && data.VirtualKeyCode is NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU)
            {
                switching = false;
                logger.Info($"Selector Alt+Tab confirmado al liberar Alt (VK {data.VirtualKeyCode}).");
                messageLoop.Post(switcher.ConfirmSelection);
                // La pulsación Tab se consume para evitar el selector del sistema,
                // pero Alt-up debe llegar a Windows y a la ventana enfocada. Si se
                // bloquea, el modificador queda retenido hasta la siguiente pulsación.
                return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            }
        }
        catch (Exception exception)
        {
            logger.Error("Error procesando el hook Alt+Tab del selector.", exception);
            switching = false;
            messageLoop.Post(switcher.CancelSelection);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
