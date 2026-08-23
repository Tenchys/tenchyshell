namespace TenchyShell.Core.SystemTray;

/// <summary>
/// Estado efímero del autocierre de la bandeja. Sólo se habilita para una
/// apertura iniciada con el mouse y no mantiene temporizadores propios.
/// </summary>
public sealed class SystemTrayAutoDismissState
{
    public bool IsEnabled { get; private set; }

    public bool IsTimerPending { get; private set; }

    public void Open(bool openedFromPointer, bool pointerOverMenu)
    {
        IsEnabled = openedFromPointer;
        IsTimerPending = openedFromPointer && !pointerOverMenu;
    }

    public void PointerEntered()
    {
        IsTimerPending = false;
    }

    public void PointerLeft()
    {
        if (IsEnabled) IsTimerPending = true;
    }

    public void KeyboardInteraction()
    {
        IsEnabled = false;
        IsTimerPending = false;
    }

    public bool TimerElapsed(bool pointerOverMenu)
    {
        IsTimerPending = false;
        return IsEnabled && !pointerOverMenu;
    }

    public void Close()
    {
        IsEnabled = false;
        IsTimerPending = false;
    }
}
