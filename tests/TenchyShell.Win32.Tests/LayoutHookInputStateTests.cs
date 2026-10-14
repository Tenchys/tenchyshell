using Xunit;

namespace TenchyShell.Win32.Tests;

public sealed class LayoutHookInputStateTests
{
    [Fact]
    public void ModifierTransitionsTrackControlAndShiftIndependently()
    {
        var state = new TenchyShell.Win32.LayoutHookInputState();

        state.ApplyModifier(NativeMethods.VK_CONTROL, isKeyDown: true, isKeyUp: false);
        state.ApplyModifier(NativeMethods.VK_SHIFT, isKeyDown: true, isKeyUp: false);

        Assert.True(state.ControlPressed);
        Assert.True(state.ShiftPressed);

        state.ApplyModifier(NativeMethods.VK_CONTROL, isKeyDown: false, isKeyUp: true);

        Assert.False(state.ControlPressed);
        Assert.True(state.ShiftPressed);
    }

    [Fact]
    public void EscapeAndModifierReleaseCancelOnlyDuringAnActiveDrag()
    {
        var state = new TenchyShell.Win32.LayoutHookInputState();

        Assert.False(state.ShouldCancelForEscape(NativeMethods.VK_ESCAPE, isKeyDown: true, isDragging: false));
        Assert.True(state.ShouldCancelForEscape(NativeMethods.VK_ESCAPE, isKeyDown: true, isDragging: true));
        Assert.False(state.ShouldCancelForEscape(NativeMethods.VK_ESCAPE, isKeyDown: false, isDragging: true));
        Assert.True(state.ShouldCancelForModifierRelease(NativeMethods.VK_SHIFT, isKeyUp: true, isDragging: true));
        Assert.False(state.ShouldCancelForModifierRelease(NativeMethods.VK_SHIFT, isKeyUp: false, isDragging: true));
        Assert.False(state.ShouldCancelForModifierRelease(NativeMethods.VK_SHIFT, isKeyUp: true, isDragging: false));
    }
}
