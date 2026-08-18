using Tomlyn;
using Tomlyn.Model;
using MinimalShell.Core.Logging;

namespace MinimalShell.Core.Configuration;

public sealed class TomlConfigurationProvider : IConfigurationProvider
{
    private readonly ILogger logger;

    public TomlConfigurationProvider(ILogger logger)
    {
        this.logger = logger;
    }

    public ConfigurationLoadResult Load(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.Info("No se especificó archivo de configuración; se usarán los valores por defecto.");
            return ConfigurationLoadResult.Success(ShellConfiguration.CreateDefault(), usedDefaults: true);
        }

        try
        {
            var text = File.ReadAllText(path);
            var table = Toml.ToModel<TomlTable>(text);
            var configuration = MapConfiguration(table);
            var errors = Validate(configuration);

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    logger.Error($"Configuración inválida: {error}");
                }

                return ConfigurationLoadResult.Invalid(ShellConfiguration.CreateDefault(), errors);
            }

            logger.Info($"Configuración cargada desde '{path}'.");
            return ConfigurationLoadResult.Success(configuration, usedDefaults: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var error = $"No se pudo leer el archivo de configuración '{path}': {exception.Message}";
            logger.Error(error, exception);
            return ConfigurationLoadResult.Invalid(ShellConfiguration.CreateDefault(), new[] { error });
        }
        catch (Exception exception)
        {
            var error = $"No se pudo analizar el archivo de configuración '{path}': {exception.Message}";
            logger.Error(error, exception);
            return ConfigurationLoadResult.Invalid(ShellConfiguration.CreateDefault(), new[] { error });
        }
    }

    private static ShellConfiguration MapConfiguration(TomlTable table)
    {
        var defaults = ShellConfiguration.CreateDefault();
        var terminal = GetTable(table, "terminal");
        var fileManager = GetTable(table, "file_manager");
        var launcher = GetTable(table, "launcher");
        var applications = GetTable(table, "applications");
        var statusPanel = GetTable(table, "status_panel");
        var hotkeys = GetTable(table, "hotkeys");
        var workspaceHotkeys = GetTable(hotkeys, "workspaces");
        var windowHotkeys = GetTable(hotkeys, "window");
        var defaultWorkspaceHotkeys = defaults.WorkspaceHotkeys;

        return new ShellConfiguration
        {
            Terminal = new TerminalConfiguration
            {
                Command = GetString(terminal, "command", defaults.Terminal.Command),
                FileManagerArguments = GetString(
                    terminal,
                    "file_manager_arguments",
                    defaults.Terminal.FileManagerArguments),
                CommandShell = GetString(terminal, "command_shell", defaults.Terminal.CommandShell),
                CommandArguments = GetString(terminal, "command_arguments", defaults.Terminal.CommandArguments)
            },
            FileManager = new FileManagerConfiguration
            {
                Command = GetString(fileManager, "command", defaults.FileManager.Command)
            },
            Launcher = new LauncherConfiguration
            {
                Enabled = GetBoolean(launcher, "enabled", defaults.Launcher.Enabled),
                Command = GetString(launcher, "command", defaults.Launcher.Command)
            },
            Applications = new ApplicationConfiguration
            {
                Browser = GetString(applications, "browser", defaults.Applications.Browser)
            },
            StatusPanel = new StatusPanelConfiguration
            {
                Enabled = GetBoolean(statusPanel, "enabled", defaults.StatusPanel.Enabled),
                Hotkey = GetString(statusPanel, "hotkey", defaults.StatusPanel.Hotkey),
                Width = GetInt(statusPanel, "width", defaults.StatusPanel.Width),
                Height = GetInt(statusPanel, "height", defaults.StatusPanel.Height),
                EdgeZone = GetInt(statusPanel, "edge_zone", defaults.StatusPanel.EdgeZone),
                Monitor = GetString(statusPanel, "monitor", defaults.StatusPanel.Monitor)
            },
            Hotkeys = new HotkeyConfiguration
            {
                Terminal = GetString(hotkeys, "terminal", defaults.Hotkeys.Terminal),
                Files = GetString(hotkeys, "files", defaults.Hotkeys.Files),
                Launcher = GetString(hotkeys, "launcher", defaults.Hotkeys.Launcher),
                Browser = GetString(hotkeys, "browser", defaults.Hotkeys.Browser),
                CloseWindow = GetString(hotkeys, "close_window", defaults.Hotkeys.CloseWindow),
                Recovery = GetString(hotkeys, "recovery", defaults.Hotkeys.Recovery)
            },
            WorkspaceHotkeys = new WorkspaceHotkeyConfiguration
            {
                Switch = Enumerable.Range(1, 9)
                    .Select(index => GetString(
                        workspaceHotkeys,
                        $"switch_{index}",
                        defaultWorkspaceHotkeys.Switch[index - 1]))
                    .ToArray(),
                Move = Enumerable.Range(1, 9)
                    .Select(index => GetString(
                        workspaceHotkeys,
                        $"move_{index}",
                        defaultWorkspaceHotkeys.Move[index - 1]))
                    .ToArray()
            },
            WindowHotkeys = new WindowHotkeyConfiguration
            {
                MoveLeft = GetString(windowHotkeys, "move_left", defaults.WindowHotkeys.MoveLeft),
                MoveRight = GetString(windowHotkeys, "move_right", defaults.WindowHotkeys.MoveRight),
                MoveUp = GetString(windowHotkeys, "move_up", defaults.WindowHotkeys.MoveUp),
                MoveDown = GetString(windowHotkeys, "move_down", defaults.WindowHotkeys.MoveDown),
                ResizeGrow = GetString(windowHotkeys, "resize_grow", defaults.WindowHotkeys.ResizeGrow),
                ResizeShrink = GetString(windowHotkeys, "resize_shrink", defaults.WindowHotkeys.ResizeShrink),
                Maximize = GetString(windowHotkeys, "maximize", defaults.WindowHotkeys.Maximize),
                Restore = GetString(windowHotkeys, "restore", defaults.WindowHotkeys.Restore),
                Focus = GetString(windowHotkeys, "focus", defaults.WindowHotkeys.Focus)
            }
        };
    }

    private static IReadOnlyList<string> Validate(ShellConfiguration configuration)
    {
        var errors = new List<string>();

        AddRequiredError(errors, "terminal.command", configuration.Terminal.Command);
        AddRequiredError(errors, "file_manager.command", configuration.FileManager.Command);
        AddRequiredError(errors, "applications.browser", configuration.Applications.Browser);
        AddRequiredError(errors, "hotkeys.terminal", configuration.Hotkeys.Terminal);
        AddRequiredError(errors, "hotkeys.files", configuration.Hotkeys.Files);
        AddRequiredError(errors, "hotkeys.launcher", configuration.Hotkeys.Launcher);
        AddRequiredError(errors, "hotkeys.browser", configuration.Hotkeys.Browser);
        AddRequiredError(errors, "hotkeys.close_window", configuration.Hotkeys.CloseWindow);
        AddRequiredError(errors, "hotkeys.recovery", configuration.Hotkeys.Recovery);
        if (configuration.StatusPanel.Enabled)
        {
            AddRequiredError(errors, "status_panel.hotkey", configuration.StatusPanel.Hotkey);
        }

        if (configuration.StatusPanel.Width <= 0)
        {
            errors.Add("'status_panel.width' debe ser mayor que cero.");
        }

        if (configuration.StatusPanel.Height <= 0)
        {
            errors.Add("'status_panel.height' debe ser mayor que cero.");
        }

        if (configuration.StatusPanel.EdgeZone < 0)
        {
            errors.Add("'status_panel.edge_zone' no puede ser negativo.");
        }

        if (!string.Equals(configuration.StatusPanel.Monitor, "primary", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("'status_panel.monitor' solo admite actualmente el valor 'primary'.");
        }

        for (var index = 0; index < 9; index++)
        {
            AddRequiredError(errors, $"hotkeys.workspaces.switch_{index + 1}", configuration.WorkspaceHotkeys.Switch[index]);
            AddRequiredError(errors, $"hotkeys.workspaces.move_{index + 1}", configuration.WorkspaceHotkeys.Move[index]);
        }

        AddRequiredError(errors, "hotkeys.window.move_left", configuration.WindowHotkeys.MoveLeft);
        AddRequiredError(errors, "hotkeys.window.move_right", configuration.WindowHotkeys.MoveRight);
        AddRequiredError(errors, "hotkeys.window.move_up", configuration.WindowHotkeys.MoveUp);
        AddRequiredError(errors, "hotkeys.window.move_down", configuration.WindowHotkeys.MoveDown);
        AddRequiredError(errors, "hotkeys.window.resize_grow", configuration.WindowHotkeys.ResizeGrow);
        AddRequiredError(errors, "hotkeys.window.resize_shrink", configuration.WindowHotkeys.ResizeShrink);
        AddRequiredError(errors, "hotkeys.window.maximize", configuration.WindowHotkeys.Maximize);
        AddRequiredError(errors, "hotkeys.window.restore", configuration.WindowHotkeys.Restore);
        AddRequiredError(errors, "hotkeys.window.focus", configuration.WindowHotkeys.Focus);

        return errors;
    }

    private static void AddRequiredError(ICollection<string> errors, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"'{key}' no puede estar vacío.");
        }
    }

    private static TomlTable GetTable(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is TomlTable nested
            ? nested
            : new TomlTable();

    private static string GetString(TomlTable table, string key, string fallback) =>
        table.TryGetValue(key, out var value) && value is string text
            ? text
            : fallback;

    private static bool GetBoolean(TomlTable table, string key, bool fallback) =>
        table.TryGetValue(key, out var value) && value is bool boolean
            ? boolean
            : fallback;

    private static int GetInt(TomlTable table, string key, int fallback) =>
        table.TryGetValue(key, out var value) && value is long number && number <= int.MaxValue && number >= int.MinValue
            ? (int)number
            : fallback;
}
