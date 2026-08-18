namespace MinimalShell.Core.Configuration;

public sealed class ShellConfiguration
{
    public TerminalConfiguration Terminal { get; init; } = new();

    public FileManagerConfiguration FileManager { get; init; } = new();

    public LauncherConfiguration Launcher { get; init; } = new();

    public ApplicationConfiguration Applications { get; init; } = new();

    public HotkeyConfiguration Hotkeys { get; init; } = new();

    public WorkspaceHotkeyConfiguration WorkspaceHotkeys { get; init; } = WorkspaceHotkeyConfiguration.CreateDefault();

    public WindowHotkeyConfiguration WindowHotkeys { get; init; } = new();

    public StatusPanelConfiguration StatusPanel { get; init; } = new();

    public static ShellConfiguration CreateDefault() => new();
}

public sealed class TerminalConfiguration
{
    public string Command { get; init; } = "wezterm-gui.exe";

    public string FileManagerArguments { get; init; } = "start --always-new-process --";

    public string CommandShell { get; init; } = "powershell.exe";

    public string CommandArguments { get; init; } = "start --always-new-process -- {shell} -NoExit -Command";
}

public sealed class FileManagerConfiguration
{
    public string Command { get; init; } = "yazi.exe";
}

public sealed class LauncherConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Command { get; init; } = string.Empty;
}

public sealed class ApplicationConfiguration
{
    public string Browser { get; init; } = "msedge.exe";
}

public sealed class HotkeyConfiguration
{
    public string Terminal { get; init; } = "Win+Enter";

    public string Files { get; init; } = "Win+E";

    public string Launcher { get; init; } = "Ctrl+Alt+Space";

    public string Browser { get; init; } = "Win+B";

    public string CloseWindow { get; init; } = "Win+Q";

    public string Recovery { get; init; } = "Ctrl+Alt+Shift+E";
}

public sealed class StatusPanelConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Hotkey { get; init; } = "Ctrl+Alt+S";

    public int Width { get; init; } = 220;

    public int Height { get; init; } = 96;

    public int EdgeZone { get; init; } = 4;

    public string Monitor { get; init; } = "primary";
}
