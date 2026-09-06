namespace TenchyShell.Core.Windows;

public interface IMinimizedWindowService
{
    MinimizedWindowMenuState GetCurrentState();

    WorkspaceFocusResult RestoreAndFocus(MinimizedWindowEntry entry);
}
