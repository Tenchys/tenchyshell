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
        var hotkeys = GetTable(table, "hotkeys");

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
            Hotkeys = new HotkeyConfiguration
            {
                Terminal = GetString(hotkeys, "terminal", defaults.Hotkeys.Terminal),
                Files = GetString(hotkeys, "files", defaults.Hotkeys.Files),
                Launcher = GetString(hotkeys, "launcher", defaults.Hotkeys.Launcher),
                Browser = GetString(hotkeys, "browser", defaults.Hotkeys.Browser),
                CloseWindow = GetString(hotkeys, "close_window", defaults.Hotkeys.CloseWindow),
                Recovery = GetString(hotkeys, "recovery", defaults.Hotkeys.Recovery)
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
}
