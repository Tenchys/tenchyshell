using TenchyShell.Core.Configuration;

namespace TenchyShell.Tui;

public static class TuiValueResolver
{
    public static string Resolve(ShellConfiguration configuration, string source, string configurationPath)
    {
        var key = source.Trim().ToLowerInvariant();
        return key switch
        {
            "configuration.path" => string.IsNullOrWhiteSpace(configurationPath)
                ? "[ausente: se usan valores incorporados]"
                : configurationPath,
            "terminal.command" => configuration.Terminal.Command,
            "terminal.shell" => configuration.Terminal.CommandShell,
            "file_manager.command" => configuration.FileManager.Command,
            "applications.browser" => configuration.Applications.Browser,
            "hotkeys.terminal" => configuration.Hotkeys.Terminal,
            "hotkeys.files" => configuration.Hotkeys.Files,
            "hotkeys.launcher" => configuration.Hotkeys.Launcher,
            "hotkeys.browser" => configuration.Hotkeys.Browser,
            "hotkeys.close_window" => configuration.Hotkeys.CloseWindow,
            "hotkeys.recovery" => configuration.Hotkeys.Recovery,
            _ => $"[ausente: {source}]"
        };
    }
}
