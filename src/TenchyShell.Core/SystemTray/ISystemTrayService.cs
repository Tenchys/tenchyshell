namespace TenchyShell.Core.SystemTray;

public interface ISystemTrayService
{
    bool TryOpen(out string? error);

    bool TryOpenInputLanguageSelector(out string? error);
}
