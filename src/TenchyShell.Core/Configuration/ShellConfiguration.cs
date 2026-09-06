using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;

namespace TenchyShell.Core.Configuration;

public sealed class ShellConfiguration
{
    public TerminalConfiguration Terminal { get; init; } = new();

    public FileManagerConfiguration FileManager { get; init; } = new();

    public LauncherConfiguration Launcher { get; init; } = new();

    public OptionalComponentsConfiguration Optionals { get; init; } = new();

    public ApplicationConfiguration Applications { get; init; } = new();

    public HotkeyConfiguration Hotkeys { get; init; } = new();

    public WorkspaceHotkeyConfiguration WorkspaceHotkeys { get; init; } = WorkspaceHotkeyConfiguration.CreateDefault();

    public WindowHotkeyConfiguration WindowHotkeys { get; init; } = new();

    public StatusPanelConfiguration StatusPanel { get; init; } = new();

    public LayoutConfiguration Layout { get; init; } = new();

    public LayoutHotkeyConfiguration LayoutHotkeys { get; init; } = new();

    public WindowSwitcherConfiguration WindowSwitcher { get; init; } = new();

    public string ConfigurationDirectory { get; init; } = Environment.CurrentDirectory;

    public string ConfigurationPath { get; init; } = string.Empty;

    public SystemTrayConfiguration SystemTray { get; init; } = new();

    public InputLanguageConfiguration InputLanguage { get; init; } = new();

    public WallpaperConfiguration Wallpaper { get; init; } = new();

    public BenchmarkConfiguration Benchmark { get; init; } = new();

    public NotificationConfiguration Notifications { get; init; } = new();

    public DockConfiguration Dock { get; init; } = DockConfiguration.CreateDefault();

    public TuiConfiguration Tui { get; init; } = new();

    public LocalApiConfiguration Api { get; init; } = new();

    public static ShellConfiguration CreateDefault() => new();
}

public sealed class DockConfiguration
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<DockCategoryConfiguration> Categories { get; init; } = Array.Empty<DockCategoryConfiguration>();

    public static DockConfiguration CreateDefault() => new()
    {
        Categories = new[]
        {
            new DockCategoryConfiguration { Id = "equipo", Title = "Equipo", Order = 10, Items = new[] {
                new DockItemConfiguration { Id = "network", Title = "Red", Action = "network" },
                new DockItemConfiguration { Id = "wallpaper", Title = "Fondos", Action = "wallpaper" },
                new DockItemConfiguration { Id = "input-language", Title = "Idioma", Action = "input-language" },
                new DockItemConfiguration { Id = "windows", Title = "Minimizadas", Action = "windows" }}},
            new DockCategoryConfiguration { Id = "aplicaciones", Title = "Aplicaciones", Order = 20, Items = new[] {
                new DockItemConfiguration { Id = "terminal", Title = "Terminal", Action = "terminal" },
                new DockItemConfiguration { Id = "files", Title = "Yazi", Action = "files" },
                new DockItemConfiguration { Id = "browser", Title = "Navegador", Action = "browser" },
                new DockItemConfiguration { Id = "launcher", Title = "Launcher", Action = "launcher" }}},
            new DockCategoryConfiguration { Id = "sesion", Title = "Sesión", Order = 30, Items = new[] {
                new DockItemConfiguration { Id = "logout", Title = "Cerrar sesión", Action = "logout" },
                new DockItemConfiguration { Id = "shutdown", Title = "Apagar", Action = "shutdown" },
                new DockItemConfiguration { Id = "restart", Title = "Reiniciar", Action = "restart" },
                new DockItemConfiguration { Id = "recovery", Title = "Recuperación", Action = "recovery" }}}
        }
    };
}

public sealed class DockCategoryConfiguration
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int Order { get; init; }
    public bool Visible { get; init; } = true;
    public IReadOnlyList<DockItemConfiguration> Items { get; init; } = Array.Empty<DockItemConfiguration>();
}

public sealed class DockItemConfiguration
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public bool Visible { get; init; } = true;
}

public sealed class BenchmarkConfiguration
{
    public bool Enabled { get; init; }

    /// <summary>Actualmente solo se admite "detailed".</summary>
    public string CaptureProfile { get; init; } = "detailed";

    public int SampleIntervalSeconds { get; init; } = 10;

    public int RetentionDays { get; init; } = 14;

    public int MaxStorageMb { get; init; } = 256;
}

public sealed class NotificationConfiguration
{
    /// <summary>Requiere el bridge MSIX opcional y autorización del usuario.</summary>
    public bool Enabled { get; init; }
}

public enum TuiWidgetType
{
    Unknown,
    Static,
    Configuration,
    Command
}

public sealed class TuiConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Title { get; init; } = "TenchyShell";

    public IReadOnlyList<TuiWidgetConfiguration> Widgets { get; init; } = Array.Empty<TuiWidgetConfiguration>();
}

public sealed class TuiWidgetConfiguration
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public TuiWidgetType Type { get; init; } = TuiWidgetType.Unknown;

    public string Source { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public int TimeoutMilliseconds { get; init; } = 1500;

    public int MaxOutputBytes { get; init; } = 4096;

    /// <summary>0 significa actualización manual desde la TUI.</summary>
    public int UpdateMilliseconds { get; init; }
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

public sealed class OptionalComponentsConfiguration
{
    public bool Dock { get; init; } = true;

    public bool ApplicationSearch { get; init; } = true;
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

public sealed class WindowSwitcherConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Hotkey { get; init; } = "Alt+Tab";

    public int Width { get; init; } = 680;

    public int Height { get; init; } = 420;

    public string TitleFormat { get; init; } = "TenchyShell — Workspace {workspace}";
}

public sealed class SystemTrayConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Hotkey { get; init; } = "Ctrl+Alt+T";

    public int Width { get; init; } = 420;

    public int Height { get; init; } = 280;

    public IReadOnlyList<SystemTrayItemConfiguration> Items { get; init; } = Array.Empty<SystemTrayItemConfiguration>();

    public IReadOnlyDictionary<string, SystemTrayActionConfiguration> Actions { get; init; } =
        new Dictionary<string, SystemTrayActionConfiguration>(StringComparer.OrdinalIgnoreCase);
}

public sealed class InputLanguageConfiguration
{
    public bool Enabled { get; init; } = true;

    public string Title { get; init; } = "Idioma";

    /// <summary>"short" muestra ES/EN; "full" muestra el nombre de Windows.</summary>
    public string LabelFormat { get; init; } = "short";

    /// <summary>Atajo opcional para abrir directamente el selector.</summary>
    public string Hotkey { get; init; } = string.Empty;
}
