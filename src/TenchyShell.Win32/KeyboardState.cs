namespace TenchyShell.Win32;

public static class KeyboardState
{
    public static bool IsAltPressed => (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
}
