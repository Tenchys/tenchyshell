using Tomlyn;
using Tomlyn.Model;
using TenchyShell.Core.Layout;
using TenchyShell.Core.Logging;
using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;

namespace TenchyShell.Core.Configuration;

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
            var configuration = MapConfiguration(
                table,
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory,
                Path.GetFullPath(path));
            var errors = new List<string>(Validate(configuration));
            errors.AddRange(ValidateOptionalComponentTypes(table));
            errors.AddRange(ValidateTuiTypes(table));
            errors.AddRange(ValidateLocalApiTypes(table));
            ValidateDockSyntax(table, errors);

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

    private static ShellConfiguration MapConfiguration(
        TomlTable table,
        string configurationDirectory = "",
        string configurationPath = "")
    {
        var defaults = ShellConfiguration.CreateDefault();
        var terminal = GetTable(table, "terminal");
        var fileManager = GetTable(table, "file_manager");
        var launcher = GetTable(table, "launcher");
        var optionals = GetTable(table, "OPTIONALS");
        var applications = GetTable(table, "applications");
        var statusPanel = GetTable(table, "status_panel");
        var layout = GetTable(table, "layout");
        var windowSwitcher = GetTable(table, "window_switcher");
        var systemTray = GetTable(table, "system_tray");
        var inputLanguage = GetTable(table, "input_language");
        var wallpaper = GetTable(table, "wallpaper");
        var benchmark = GetTable(table, "benchmark");
        var logging = GetTable(table, "logging");
        var notifications = GetTable(table, "notifications");
        var dock = GetTable(table, "dock");
        var tui = GetTable(table, "tui");
        var api = GetTable(table, "api");
        var hotkeys = GetTable(table, "hotkeys");
        var workspaceHotkeys = GetTable(hotkeys, "workspaces");
        var windowHotkeys = GetTable(hotkeys, "window");
        var layoutHotkeys = GetTable(hotkeys, "layout");
        var defaultWorkspaceHotkeys = defaults.WorkspaceHotkeys;

        return new ShellConfiguration
        {
            ConfigurationDirectory = string.IsNullOrWhiteSpace(configurationDirectory)
                ? Environment.CurrentDirectory
                : configurationDirectory,
            ConfigurationPath = configurationPath,
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
            Optionals = new OptionalComponentsConfiguration
            {
                Dock = GetBoolean(optionals, "dock", defaults.Optionals.Dock),
                ApplicationSearch = GetBoolean(optionals, "application_search", defaults.Optionals.ApplicationSearch)
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
            Layout = new LayoutConfiguration
            {
                Enabled = GetBoolean(layout, "enabled", defaults.Layout.Enabled),
                MaxZones = GetInt(layout, "max_zones", defaults.Layout.MaxZones),
                DefaultPreset = GetString(layout, "default_preset", defaults.Layout.DefaultPreset),
                ZoneNumberSizePercent = GetDouble(
                    layout,
                    "zone_number_size_percent",
                    defaults.Layout.ZoneNumberSizePercent),
                Zones = GetLayoutZones(layout)
            },
            LayoutHotkeys = new LayoutHotkeyConfiguration
            {
                Zones = Enumerable.Range(1, 9)
                    .Select(index => GetString(
                        layoutHotkeys,
                        $"zone_{index}",
                        defaults.LayoutHotkeys.Zones[index - 1]))
                    .ToArray(),
                DragModifier = GetString(layoutHotkeys, "drag_modifier", defaults.LayoutHotkeys.DragModifier)
            },
            WindowSwitcher = new WindowSwitcherConfiguration
            {
                Enabled = GetBoolean(windowSwitcher, "enabled", defaults.WindowSwitcher.Enabled),
                Hotkey = GetString(windowSwitcher, "hotkey", defaults.WindowSwitcher.Hotkey),
                Width = GetInt(windowSwitcher, "width", defaults.WindowSwitcher.Width),
                Height = GetInt(windowSwitcher, "height", defaults.WindowSwitcher.Height),
                TitleFormat = GetString(windowSwitcher, "title_format", defaults.WindowSwitcher.TitleFormat)
            },
            SystemTray = new SystemTrayConfiguration
            {
                Enabled = GetBoolean(systemTray, "enabled", defaults.SystemTray.Enabled),
                Hotkey = GetString(systemTray, "hotkey", defaults.SystemTray.Hotkey),
                Width = GetInt(systemTray, "width", defaults.SystemTray.Width),
                Height = GetInt(systemTray, "height", defaults.SystemTray.Height),
                Items = GetSystemTrayItems(systemTray),
                Actions = GetSystemTrayActions(systemTray)
            },
            InputLanguage = new InputLanguageConfiguration
            {
                Enabled = GetBoolean(inputLanguage, "enabled", defaults.InputLanguage.Enabled),
                Title = GetString(inputLanguage, "title", defaults.InputLanguage.Title),
                LabelFormat = GetString(inputLanguage, "label_format", defaults.InputLanguage.LabelFormat),
                Hotkey = GetString(inputLanguage, "hotkey", defaults.InputLanguage.Hotkey)
            },
            Wallpaper = new WallpaperConfiguration
            {
                Enabled = GetBoolean(wallpaper, "enabled", defaults.Wallpaper.Enabled),
                Folder = GetString(wallpaper, "folder", defaults.Wallpaper.Folder),
                Extensions = GetStringArray(wallpaper, "extensions") is { Count: > 0 } extensions
                    ? extensions.Select(extension => extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}").ToArray()
                    : defaults.Wallpaper.Extensions,
                Monitor = GetString(wallpaper, "monitor", defaults.Wallpaper.Monitor)
            },
            Benchmark = new BenchmarkConfiguration
            {
                Enabled = GetBoolean(benchmark, "enabled", defaults.Benchmark.Enabled),
                CaptureProfile = GetString(benchmark, "capture_profile", defaults.Benchmark.CaptureProfile),
                SampleIntervalSeconds = GetInt(benchmark, "sample_interval_seconds", defaults.Benchmark.SampleIntervalSeconds),
                RetentionDays = GetInt(benchmark, "retention_days", defaults.Benchmark.RetentionDays),
                MaxStorageMb = GetInt(benchmark, "max_storage_mb", defaults.Benchmark.MaxStorageMb)
            },
            Logging = new LoggingConfiguration
            {
                Console = GetBoolean(logging, "console", defaults.Logging.Console)
            },
            Notifications = new NotificationConfiguration
            {
                Enabled = GetBoolean(notifications, "enabled", defaults.Notifications.Enabled)
            },
            Dock = GetDockConfiguration(table, dock, defaults.Dock),
            Tui = new TuiConfiguration
            {
                Enabled = GetBoolean(tui, "enabled", defaults.Tui.Enabled),
                Title = GetString(tui, "title", defaults.Tui.Title),
                Widgets = GetTuiWidgets(tui)
            },
            Api = new LocalApiConfiguration
            {
                Enabled = GetBoolean(api, "enabled", defaults.Api.Enabled)
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
        ValidateDock(errors, configuration.Dock);
        if (configuration.Benchmark.Enabled)
        {
            if (!string.Equals(configuration.Benchmark.CaptureProfile, "detailed", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("'benchmark.capture_profile' solo admite 'detailed'.");
            }
            if (configuration.Benchmark.SampleIntervalSeconds is < 1 or > 3600)
            {
                errors.Add("'benchmark.sample_interval_seconds' debe estar entre 1 y 3600.");
            }
            if (configuration.Benchmark.RetentionDays is < 1 or > 365)
            {
                errors.Add("'benchmark.retention_days' debe estar entre 1 y 365.");
            }
            if (configuration.Benchmark.MaxStorageMb is < 16 or > 4096)
            {
                errors.Add("'benchmark.max_storage_mb' debe estar entre 16 y 4096.");
            }
        }
        if (configuration.Tui.Enabled && string.IsNullOrWhiteSpace(configuration.Tui.Title))
        {
            errors.Add("'tui.title' no puede estar vacío.");
        }
        var widgetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var widget in configuration.Tui.Widgets)
        {
            if (string.IsNullOrWhiteSpace(widget.Id))
            {
                errors.Add("'tui.widgets.id' no puede estar vacío.");
            }
            else if (!widgetIds.Add(widget.Id))
            {
                errors.Add($"'tui.widgets.id' está duplicado: '{widget.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(widget.Title))
            {
                errors.Add($"'tui.widgets.{widget.Id}.title' no puede estar vacío.");
            }

            if (widget.Type == TuiWidgetType.Unknown)
            {
                errors.Add($"'tui.widgets.{widget.Id}.type' debe ser 'static', 'configuration' o 'command'.");
            }
            if ((widget.Type is TuiWidgetType.Static or TuiWidgetType.Configuration) && string.IsNullOrWhiteSpace(widget.Source))
            {
                errors.Add($"'tui.widgets.{widget.Id}.source' no puede estar vacío.");
            }
            if (widget.Type == TuiWidgetType.Command && string.IsNullOrWhiteSpace(widget.Command))
            {
                errors.Add($"'tui.widgets.{widget.Id}.command' no puede estar vacío.");
            }
            if (widget.TimeoutMilliseconds is < 100 or > 60000)
            {
                errors.Add($"'tui.widgets.{widget.Id}.timeout_ms' debe estar entre 100 y 60000.");
            }
            if (widget.MaxOutputBytes is < 64 or > 1_048_576)
            {
                errors.Add($"'tui.widgets.{widget.Id}.max_output_bytes' debe estar entre 64 y 1048576.");
            }
            if (widget.UpdateMilliseconds is < 0 or > 3_600_000)
            {
                errors.Add($"'tui.widgets.{widget.Id}.update_ms' debe estar entre 0 y 3600000.");
            }
        }
        if (configuration.Layout.Enabled)
        {
            if (!string.Equals(configuration.Layout.DefaultPreset, "1x2", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("'layout.default_preset' solo admite actualmente el valor '1x2'.");
            }

            var layoutErrors = LayoutZoneValidator.Validate(configuration.Layout.Zones, configuration.Layout.MaxZones);
            errors.AddRange(layoutErrors.Errors);

            if (configuration.Layout.ZoneNumberSizePercent <= 0 ||
                configuration.Layout.ZoneNumberSizePercent > 25)
            {
                errors.Add("'layout.zone_number_size_percent' debe ser mayor que 0 y no superar 25.");
            }

            for (var index = 0; index < 9; index++)
            {
                AddRequiredError(errors, $"hotkeys.layout.zone_{index + 1}", configuration.LayoutHotkeys.Zones[index]);
            }

            AddRequiredError(errors, "hotkeys.layout.drag_modifier", configuration.LayoutHotkeys.DragModifier);
        }

        if (configuration.StatusPanel.Enabled)
        {
            AddRequiredError(errors, "status_panel.hotkey", configuration.StatusPanel.Hotkey);
        }

        if (configuration.WindowSwitcher.Enabled)
        {
            AddRequiredError(errors, "window_switcher.hotkey", configuration.WindowSwitcher.Hotkey);
        }

        if (configuration.SystemTray.Enabled)
        {
            AddRequiredError(errors, "system_tray.hotkey", configuration.SystemTray.Hotkey);
        }

        if (configuration.SystemTray.Width <= 0)
        {
            errors.Add("'system_tray.width' debe ser mayor que cero.");
        }

        if (configuration.SystemTray.Height <= 0)
        {
            errors.Add("'system_tray.height' debe ser mayor que cero.");
        }

        if (configuration.InputLanguage.Enabled)
        {
            AddRequiredError(errors, "input_language.title", configuration.InputLanguage.Title);
            if (!string.Equals(configuration.InputLanguage.LabelFormat, "short", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(configuration.InputLanguage.LabelFormat, "full", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("'input_language.label_format' solo admite 'short' o 'full'.");
            }
        }

        var trayIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in configuration.SystemTray.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                errors.Add("'system_tray.items.id' no puede estar vacío.");
            }
            else if (!trayIds.Add(item.Id))
            {
                errors.Add($"'system_tray.items.id' está duplicado: '{item.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                errors.Add($"'system_tray.items.{item.Id}.title' no puede estar vacío.");
            }

            if (item.IntervalMilliseconds is < 250 or > 600000)
            {
                errors.Add($"'system_tray.items.{item.Id}.interval_ms' debe estar entre 250 y 600000.");
            }

            if (item.TimeoutMilliseconds is < 100 or > 60000)
            {
                errors.Add($"'system_tray.items.{item.Id}.timeout_ms' debe estar entre 100 y 60000.");
            }
        }

        foreach (var action in configuration.SystemTray.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Value.Command))
            {
                errors.Add($"'system_tray.actions.{action.Key}.command' no puede estar vacío.");
            }
        }

        if (configuration.WindowSwitcher.Width <= 0)
        {
            errors.Add("'window_switcher.width' debe ser mayor que cero.");
        }

        if (configuration.WindowSwitcher.Height <= 0)
        {
            errors.Add("'window_switcher.height' debe ser mayor que cero.");
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

    private static DockConfiguration GetDockConfiguration(TomlTable root, TomlTable dock, DockConfiguration fallback)
    {
        var hasCategories = dock.TryGetValue("categories", out var raw) && raw is TomlTableArray;
        if (!hasCategories) return new DockConfiguration { Enabled = GetBoolean(dock, "enabled", fallback.Enabled), Categories = fallback.Categories };
        var categories = new List<DockCategoryConfiguration>();
        foreach (var value in (TomlTableArray)raw!)
        {
            if (value is not TomlTable category) { categories.Add(new DockCategoryConfiguration()); continue; }
            var items = new List<DockItemConfiguration>();
            if (category.TryGetValue("items", out var itemValue) && itemValue is TomlTableArray itemArray)
            {
                foreach (var itemValueEntry in itemArray)
                {
                    if (itemValueEntry is TomlTable item)
                        items.Add(new DockItemConfiguration { Id = GetString(item, "id", string.Empty), Title = GetString(item, "title", string.Empty), Action = GetString(item, "action", string.Empty), Visible = GetBoolean(item, "visible", true) });
                }
            }
            categories.Add(new DockCategoryConfiguration { Id = GetString(category, "id", string.Empty), Title = GetString(category, "title", string.Empty), Order = GetInt(category, "order", 0), Visible = GetBoolean(category, "visible", true), Items = items });
        }
        return new DockConfiguration { Enabled = GetBoolean(dock, "enabled", fallback.Enabled), Categories = categories };
    }

    private static void ValidateDock(ICollection<string> errors, DockConfiguration dock)
    {
        if (!dock.Enabled) return;
        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryOrders = new HashSet<int>();
        var allowedCategories = new HashSet<string>(new[] { "equipo", "aplicaciones", "sesion" }, StringComparer.OrdinalIgnoreCase);
        var allowedActions = new HashSet<string>(new[] { "network", "wallpaper", "input-language", "windows", "terminal", "files", "browser", "launcher", "logout", "shutdown", "restart", "recovery" }, StringComparer.OrdinalIgnoreCase);
        var allowedItems = new HashSet<string>(new[] { "network", "wallpaper", "input-language", "windows", "terminal", "files", "browser", "launcher", "logout", "shutdown", "restart", "recovery" }, StringComparer.OrdinalIgnoreCase);
        foreach (var category in dock.Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Id)) errors.Add("'dock.categories.id' no puede estar vacío.");
            else if (!categoryIds.Add(category.Id)) errors.Add($"'dock.categories.id' está duplicado: '{category.Id}'.");
            if (!allowedCategories.Contains(category.Id)) errors.Add($"'dock.categories.{category.Id}' no es una categoría permitida.");
            if (category.Order <= 0) errors.Add($"'dock.categories.{category.Id}.order' debe ser mayor que cero.");
            else if (!categoryOrders.Add(category.Order)) errors.Add($"'dock.categories.{category.Id}.order' está duplicado: '{category.Order}'.");
            if (string.IsNullOrWhiteSpace(category.Title)) errors.Add($"'dock.categories.{category.Id}.title' no puede estar vacío.");
            var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in category.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) errors.Add($"'dock.categories.{category.Id}.items.id' no puede estar vacío.");
                else if (!itemIds.Add(item.Id)) errors.Add($"'dock.categories.{category.Id}.items.id' está duplicado: '{item.Id}'.");
                if (string.IsNullOrWhiteSpace(item.Title)) errors.Add($"'dock.categories.{category.Id}.items.{item.Id}.title' no puede estar vacío.");
                if (!allowedItems.Contains(item.Id)) errors.Add($"'dock.categories.{category.Id}.items.{item.Id}' no es un elemento permitido.");
                if (!allowedActions.Contains(item.Action)) errors.Add($"'dock.categories.{category.Id}.items.{item.Id}.action' no es una acción permitida.");
            }
        }
    }

    private static void ValidateDockSyntax(TomlTable root, ICollection<string> errors)
    {
        if (!root.TryGetValue("dock", out var rawDock)) return;
        if (rawDock is not TomlTable dock) { errors.Add("'dock' debe ser una tabla."); return; }
        if (dock.TryGetValue("enabled", out var enabled) && enabled is not bool) errors.Add("'dock.enabled' debe ser booleano.");
        if (!dock.TryGetValue("categories", out var rawCategories)) return;
        if (rawCategories is not TomlTableArray categories) { errors.Add("'dock.categories' debe ser un array de tablas."); return; }
        foreach (var rawCategory in categories)
        {
            if (rawCategory is not TomlTable category) continue;
            if (category.TryGetValue("items", out var rawItems) && rawItems is not TomlTableArray)
                errors.Add("'dock.categories.items' debe ser un array de tablas.");
        }
    }

    private static IReadOnlyList<string> ValidateOptionalComponentTypes(TomlTable table)
    {
        var errors = new List<string>();
        var optionals = GetTable(table, "OPTIONALS");
        AddBooleanTypeError(errors, optionals, "dock");
        AddBooleanTypeError(errors, optionals, "application_search");
        return errors;
    }

    private static IReadOnlyList<string> ValidateTuiTypes(TomlTable table)
    {
        var errors = new List<string>();
        var tui = GetTable(table, "tui");
        AddBooleanTypeError(errors, tui, "enabled", "tui");
        if (!tui.TryGetValue("widgets", out var value) || value is not TomlTableArray array)
        {
            return errors;
        }

        foreach (var item in array)
        {
            if (item is not TomlTable widget) continue;
            AddStringTypeError(errors, widget, "id", "tui.widgets");
            AddStringTypeError(errors, widget, "title", "tui.widgets");
            AddStringTypeError(errors, widget, "type", "tui.widgets");
            AddStringTypeError(errors, widget, "source", "tui.widgets");
            AddStringTypeError(errors, widget, "command", "tui.widgets");
            AddIntegerTypeError(errors, widget, "timeout_ms", "tui.widgets");
            AddIntegerTypeError(errors, widget, "max_output_bytes", "tui.widgets");
            AddIntegerTypeError(errors, widget, "update_ms", "tui.widgets");
            if (widget.TryGetValue("arguments", out var arguments) && arguments is not TomlArray)
            {
                errors.Add("'tui.widgets.arguments' debe ser un array de textos.");
            }
            else if (arguments is TomlArray argumentArray && argumentArray.Any(item => item is not string))
            {
                errors.Add("'tui.widgets.arguments' debe ser un array de textos.");
            }
        }
        return errors;
    }

    private static IReadOnlyList<string> ValidateLocalApiTypes(TomlTable table)
    {
        var errors = new List<string>();
        AddBooleanTypeError(errors, GetTable(table, "api"), "enabled", "api");
        return errors;
    }

    private static void AddBooleanTypeError(ICollection<string> errors, TomlTable table, string key, string section)
    {
        if (table.TryGetValue(key, out var value) && value is not bool)
        {
            errors.Add($"'{section}.{key}' debe ser booleano (true o false).");
        }
    }

    private static void AddStringTypeError(ICollection<string> errors, TomlTable table, string key, string section)
    {
        if (table.TryGetValue(key, out var value) && value is not string)
        {
            errors.Add($"'{section}.{key}' debe ser texto.");
        }
    }

    private static void AddIntegerTypeError(ICollection<string> errors, TomlTable table, string key, string section)
    {
        if (table.TryGetValue(key, out var value) && value is not long)
        {
            errors.Add($"'{section}.{key}' debe ser entero.");
        }
    }

    private static void AddBooleanTypeError(ICollection<string> errors, TomlTable table, string key)
    {
        if (table.TryGetValue(key, out var value) && value is not bool)
        {
            errors.Add($"'OPTIONALS.{key}' debe ser booleano (true o false).");
        }
    }

    private static IReadOnlyList<TuiWidgetConfiguration> GetTuiWidgets(TomlTable tui)
    {
        if (!tui.TryGetValue("widgets", out var value) || value is not TomlTableArray array)
        {
            return Array.Empty<TuiWidgetConfiguration>();
        }

        return array.OfType<TomlTable>().Select(widget => new TuiWidgetConfiguration
        {
            Id = GetString(widget, "id", string.Empty),
            Title = GetString(widget, "title", string.Empty),
            Type = ParseTuiWidgetType(GetString(widget, "type", string.Empty)),
            Source = GetString(widget, "source", string.Empty),
            Command = GetString(widget, "command", string.Empty),
            Arguments = GetStringArray(widget, "arguments"),
            TimeoutMilliseconds = GetInt(widget, "timeout_ms", 1500),
            MaxOutputBytes = GetInt(widget, "max_output_bytes", 4096),
            UpdateMilliseconds = GetInt(widget, "update_ms", 0)
        }).ToArray();
    }

    private static TuiWidgetType ParseTuiWidgetType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "static" => TuiWidgetType.Static,
        "configuration" or "config" => TuiWidgetType.Configuration,
        "command" or "external" => TuiWidgetType.Command,
        _ => TuiWidgetType.Unknown
    };

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

    private static double GetDouble(TomlTable table, string key, double fallback) =>
        table.TryGetValue(key, out var value) && value is double number
            ? number
            : table.TryGetValue(key, out value) && value is long integer
                ? integer
                : fallback;

    private static IReadOnlyList<LayoutZone> GetLayoutZones(TomlTable layout)
    {
        if (!layout.TryGetValue("zones", out var value) || value is not TomlTableArray array)
        {
            return Array.Empty<LayoutZone>();
        }

        var zones = new List<LayoutZone>();
        foreach (var item in array)
        {
            if (item is not TomlTable zone)
            {
                zones.Add(new LayoutZone(0, string.Empty, double.NaN, double.NaN, double.NaN, double.NaN));
                continue;
            }

            zones.Add(new LayoutZone(
                GetInt(zone, "number", 0),
                GetString(zone, "monitor", string.Empty),
                GetDouble(zone, "left", double.NaN),
                GetDouble(zone, "top", double.NaN),
                GetDouble(zone, "right", double.NaN),
                GetDouble(zone, "bottom", double.NaN)));
        }

        return zones;
    }

    private static IReadOnlyList<SystemTrayItemConfiguration> GetSystemTrayItems(TomlTable systemTray)
    {
        if (!systemTray.TryGetValue("items", out var value) || value is not TomlTableArray array)
        {
            return Array.Empty<SystemTrayItemConfiguration>();
        }

        var items = new List<SystemTrayItemConfiguration>();
        foreach (var item in array)
        {
            if (item is not TomlTable table)
            {
                continue;
            }

            items.Add(new SystemTrayItemConfiguration
            {
                Id = GetString(table, "id", string.Empty),
                Title = GetString(table, "title", string.Empty),
                Text = GetString(table, "text", string.Empty),
                Tooltip = GetString(table, "tooltip", string.Empty),
                Icon = GetString(table, "icon", string.Empty),
                DefaultIcon = GetString(table, "default_icon", string.Empty),
                Command = GetString(table, "command", string.Empty),
                Arguments = GetStringArray(table, "arguments"),
                IntervalMilliseconds = GetInt(table, "interval_ms", 5000),
                TimeoutMilliseconds = GetInt(table, "timeout_ms", 1500)
            });
        }

        return items;
    }

    private static IReadOnlyDictionary<string, SystemTrayActionConfiguration> GetSystemTrayActions(TomlTable systemTray)
    {
        var actions = new Dictionary<string, SystemTrayActionConfiguration>(StringComparer.OrdinalIgnoreCase);
        var groups = GetTable(systemTray, "actions");
        foreach (var group in groups)
        {
            if (group.Value is not TomlTable actionTable)
            {
                continue;
            }

            foreach (var action in actionTable)
            {
                if (action.Value is not TomlTable actionConfiguration)
                {
                    continue;
                }

                actions[$"{group.Key}.{action.Key}"] = new SystemTrayActionConfiguration
                {
                    Command = GetString(actionConfiguration, "command", string.Empty),
                    Arguments = GetStringArray(actionConfiguration, "arguments")
                };
            }
        }

        return actions;
    }

    private static IReadOnlyList<string> GetStringArray(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not TomlArray array)
        {
            return Array.Empty<string>();
        }

        return array.OfType<string>().ToArray();
    }
}
