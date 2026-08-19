namespace MinimalShell.Core.Layout;

public sealed class LayoutDragStateMachine
{
    private IntPtr windowHandle;
    private int? hoveredZone;

    public bool IsDragging => windowHandle != IntPtr.Zero;

    public IntPtr WindowHandle => windowHandle;

    public int? HoveredZone => hoveredZone;

    public bool Begin(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || IsDragging)
        {
            return false;
        }

        windowHandle = targetWindow;
        hoveredZone = null;
        return true;
    }

    public bool SetHoveredZone(int? zoneNumber)
    {
        if (!IsDragging || (zoneNumber.HasValue && zoneNumber.Value < 1))
        {
            return false;
        }

        hoveredZone = zoneNumber;
        return true;
    }

    public bool TryComplete(out IntPtr targetWindow, out int zoneNumber)
    {
        targetWindow = windowHandle;
        zoneNumber = hoveredZone ?? 0;

        if (targetWindow == IntPtr.Zero || zoneNumber == 0)
        {
            targetWindow = IntPtr.Zero;
            zoneNumber = 0;
            return false;
        }

        Reset();
        return true;
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        windowHandle = IntPtr.Zero;
        hoveredZone = null;
    }
}
