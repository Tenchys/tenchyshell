namespace TenchyShell.Core.StatusPanel;

/// <summary>Cadencia del único timer del panel, según su estado observable.</summary>
public static class StatusPanelTimerPolicy
{
    public const uint VisibleIntervalMilliseconds = 250;
    public const uint HiddenIntervalMilliseconds = 500;

    public static uint GetIntervalMilliseconds(bool visible) =>
        visible ? VisibleIntervalMilliseconds : HiddenIntervalMilliseconds;
}
