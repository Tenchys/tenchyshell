namespace TenchyShell.Win32;

/// <summary>
/// Estado puro de entrada que acompaña a los hooks del layout. Mantenerlo
/// separado permite probar las transiciones sin crear hooks ni ventanas nativas.
/// </summary>
internal sealed class LayoutHookInputState
{
    public bool ControlPressed { get; private set; }

    public bool ShiftPressed { get; private set; }

    public void ApplyModifier(uint virtualKeyCode, bool isKeyDown, bool isKeyUp)
    {
        if (!isKeyDown && !isKeyUp)
        {
            return;
        }

        var pressed = isKeyDown || !isKeyUp;
        if (IsControlVirtualKey(virtualKeyCode))
        {
            ControlPressed = pressed;
        }
        else if (IsShiftVirtualKey(virtualKeyCode))
        {
            ShiftPressed = pressed;
        }
    }

    public bool ShouldCancelForEscape(uint virtualKeyCode, bool isKeyDown, bool isDragging) =>
        isDragging && isKeyDown && virtualKeyCode == NativeMethods.VK_ESCAPE;

    public bool ShouldCancelForModifierRelease(uint virtualKeyCode, bool isKeyUp, bool isDragging) =>
        isDragging && isKeyUp && (IsControlVirtualKey(virtualKeyCode) || IsShiftVirtualKey(virtualKeyCode));

    public static bool IsControlVirtualKey(uint virtualKeyCode) => virtualKeyCode is
        NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL;

    public static bool IsShiftVirtualKey(uint virtualKeyCode) => virtualKeyCode is
        NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT;
}
