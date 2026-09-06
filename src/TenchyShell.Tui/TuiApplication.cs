using TenchyShell.Core.Configuration;
using TenchyShell.Core.Yazi;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Runtime;
using TenchyShell.Core.LocalApi;

namespace TenchyShell.Tui;

public sealed class TuiApplication
{
    public const string InstanceName = "Local\\TenchyShell.Tui.SingleInstance";

    private readonly ShellConfiguration configuration;
    private readonly IReadOnlyList<string> configurationErrors;
    private readonly string configurationPath;
    private readonly ILogger logger;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly IWidgetCommandRunner commandRunner;
    private readonly Dictionary<string, WidgetState> widgetStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> pendingUpdates = new();
    private readonly List<Timer> updateTimers = new();
    private readonly object updateSync = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<ILocalApiClient> apiClientFactory;
    private readonly Func<YaziShortcutReadResult> yaziReader;
    private readonly Func<int> widthProvider;
    private readonly Func<int> heightProvider;
    private readonly Func<string?> telemetryScriptPathResolver;
    private ILocalApiClient? apiClient;
    private LocalApiSnapshot? apiSnapshot;
    private string? apiError;
    private TuiMenu mainMenu;
    private TuiMenu? widgetMenu;
    private Screen screen;
    private YaziShortcutReadResult? yaziResult;
    private readonly TuiListViewport yaziViewport = new();
    private readonly TuiListViewport telemetryViewport = new();
    private readonly WidgetState telemetryState = new(new TuiWidgetConfiguration { Id = "telemetry", Title = "Telemetría", Type = TuiWidgetType.Command });
    private string? renderedTelemetryOutput;
    private int bufferedInput = -1;

    public TuiApplication(
        ShellConfiguration configuration,
        IReadOnlyList<string> configurationErrors,
        string configurationPath,
        ILogger logger,
        TextReader? input = null,
        TextWriter? output = null,
        IWidgetCommandRunner? commandRunner = null,
        Func<ILocalApiClient>? apiClientFactory = null,
        Func<YaziShortcutReadResult>? yaziReader = null,
        Func<int>? widthProvider = null,
        Func<int>? heightProvider = null,
        Func<string?>? telemetryScriptPathResolver = null)
    {
        this.configuration = configuration;
        this.configurationErrors = configurationErrors;
        this.configurationPath = configurationPath;
        this.logger = logger;
        this.input = input ?? Console.In;
        this.output = output ?? Console.Out;
        this.commandRunner = commandRunner ?? new ExternalWidgetCommandRunner();
        this.apiClientFactory = apiClientFactory ?? (() => new NamedPipeLocalApiClient());
        this.yaziReader = yaziReader ?? (() => new YaziShortcutReader().Read());
        this.widthProvider = widthProvider ?? GetWidth;
        this.heightProvider = heightProvider ?? GetHeight;
        this.telemetryScriptPathResolver = telemetryScriptPathResolver ?? (() => TelemetrySummaryScriptLocator.Find(configurationPath));
        mainMenu = new TuiMenu(new[]
        {
            new TuiMenuItem("info", "Información"),
            new TuiMenuItem("api", "API local"),
            new TuiMenuItem("widgets", "Widgets"),
            new TuiMenuItem("telemetry", "Telemetría"),
            new TuiMenuItem("yazi", "Atajos de Yazi"),
            new TuiMenuItem("help", "Ayuda"),
            new TuiMenuItem("exit", "Salir")
        });
        screen = Screen.Main;
        foreach (var widget in configuration.Tui.Widgets)
        {
            var state = new WidgetState(widget);
            widgetStates[widget.Id] = state;
            if (widget.UpdateMilliseconds > 0)
            {
                updateTimers.Add(new Timer(
                    _ => StartRefresh(state),
                    null,
                    widget.UpdateMilliseconds,
                    widget.UpdateMilliseconds));
            }
        }
    }

    public TuiMenu MainMenu => mainMenu;

    public static int Run(string configurationPath, ILogger logger)
    {
        if (!SingleInstanceGuard.TryAcquire(InstanceName, out var guard))
        {
            const string message = "La TUI de TenchyShell ya está ejecutándose.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        try
        {
            var provider = new TomlConfigurationProvider(logger);
            ConfigurationLoadResult loaded;
            if (File.Exists(configurationPath))
            {
                loaded = provider.Load(configurationPath);
            }
            else
            {
                loaded = provider.Load();
                logger.Info($"La TUI no encontró '{configurationPath}'; muestra valores incorporados.");
            }

            return new TuiApplication(
                loaded.Configuration,
                loaded.Errors,
                configurationPath,
                logger).RunInteractive();
        }
        catch (Exception exception)
        {
            logger.Error("La TUI no pudo iniciar.", exception);
            Console.Error.WriteLine($"No se pudo iniciar la TUI: {exception.Message}");
            return 1;
        }
        finally
        {
            guard!.Dispose();
        }
    }

    public int RunInteractive()
    {
        try
        {
            TrySetCursorVisible(false);
            while (screen != Screen.Exit)
            {
                Render();
                var key = ReadKey();
                HandleKey(key);
            }
            RenderExit();
            return 0;
        }
        catch (Exception exception)
        {
            logger.Error("Error en el ciclo de vida de la TUI.", exception);
            output.WriteLine($"Error de la TUI: {exception.Message}");
            return 1;
        }
        finally
        {
            foreach (var timer in updateTimers) timer.Dispose();
            LeaveApi();
            cancellation.Cancel();
            Task[] updates;
            lock (updateSync) updates = pendingUpdates.ToArray();
            try { Task.WhenAll(updates).GetAwaiter().GetResult(); }
            catch (Exception exception) { logger.Error("Falló la limpieza de widgets de la TUI.", exception); }
            TrySetCursorVisible(true);
            cancellation.Dispose();
        }
    }

    public void HandleKey(ConsoleKey key)
    {
        if (screen == Screen.Main)
        {
            switch (key)
            {
                case ConsoleKey.UpArrow: mainMenu.MovePrevious(); break;
                case ConsoleKey.DownArrow: mainMenu.MoveNext(); break;
                case ConsoleKey.Enter: ActivateMainItem(); break;
                case ConsoleKey.Escape: screen = Screen.Exit; break;
            }
            return;
        }

        if (key is ConsoleKey.Escape or ConsoleKey.Backspace)
        {
            if (screen == Screen.Api) LeaveApi();
            screen = Screen.Main;
            return;
        }
        if (key is ConsoleKey.Q)
        {
            if (screen == Screen.Api) LeaveApi();
            screen = Screen.Exit;
            return;
        }
        if (screen == Screen.Yazi)
        {
            switch (key)
            {
                case ConsoleKey.UpArrow: yaziViewport.Move(-1); break;
                case ConsoleKey.DownArrow: yaziViewport.Move(1); break;
                case ConsoleKey.PageUp: yaziViewport.PageUp(); break;
                case ConsoleKey.PageDown: yaziViewport.PageDown(); break;
                case ConsoleKey.Home: yaziViewport.Home(); break;
                case ConsoleKey.End: yaziViewport.End(); break;
                case ConsoleKey.R: LoadYazi(true); break;
            }
            return;
        }
        if (screen == Screen.Telemetry)
        {
            switch (key)
            {
                case ConsoleKey.UpArrow: telemetryViewport.Move(-1); break;
                case ConsoleKey.DownArrow: telemetryViewport.Move(1); break;
                case ConsoleKey.PageUp: telemetryViewport.PageUp(); break;
                case ConsoleKey.PageDown: telemetryViewport.PageDown(); break;
                case ConsoleKey.Home: telemetryViewport.Home(); break;
                case ConsoleKey.End: telemetryViewport.End(); break;
                case ConsoleKey.R: StartTelemetryRefresh(); break;
            }
            return;
        }
        if (screen == Screen.Api)
        {
            if (key == ConsoleKey.R) EnterApi();
            return;
        }
        if (screen != Screen.Widgets || widgetMenu is null) return;
        switch (key)
        {
            case ConsoleKey.UpArrow: widgetMenu.MovePrevious(); break;
            case ConsoleKey.DownArrow: widgetMenu.MoveNext(); break;
            case ConsoleKey.Enter:
            case ConsoleKey.R: RefreshSelected(); break;
            case ConsoleKey.A: RefreshAll(); break;
        }
    }

    private void ActivateMainItem()
    {
        switch (mainMenu.SelectedItem?.Id)
        {
            case "info": screen = Screen.Info; break;
            case "api": EnterApi(); break;
            case "widgets":
                widgetMenu = new TuiMenu(configuration.Tui.Widgets.Select(widget => new TuiMenuItem(widget.Id, widget.Title)));
                screen = Screen.Widgets;
                break;
            case "telemetry":
                screen = Screen.Telemetry;
                StartTelemetryRefresh();
                break;
            case "yazi": LoadYazi(false); screen = Screen.Yazi; break;
            case "help": screen = Screen.Help; break;
            case "exit": screen = Screen.Exit; break;
        }
    }

    private void RefreshSelected()
    {
        var id = widgetMenu?.SelectedItem?.Id;
        if (id is null || !widgetStates.TryGetValue(id, out var state)) return;
        StartRefresh(state);
    }

    private void RefreshAll()
    {
        foreach (var state in widgetStates.Values) StartRefresh(state);
    }

    private void StartRefresh(WidgetState state)
    {
        lock (updateSync)
        {
            if (state.Pending || cancellation.IsCancellationRequested) return;
            state.Pending = true;
            var task = Task.Run(() => RefreshAsync(state), CancellationToken.None);
            pendingUpdates.Add(task);
            _ = task.ContinueWith(
                _ =>
                {
                    lock (updateSync) pendingUpdates.Remove(task);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RefreshAsync(WidgetState state)
    {
        try
        {
            var result = state.Configuration.Type switch
            {
                TuiWidgetType.Static => WidgetExecutionResult.Success(state.Configuration.Source),
                TuiWidgetType.Configuration => WidgetExecutionResult.Success(
                    TuiValueResolver.Resolve(configuration, state.Configuration.Source, configurationPath)),
                TuiWidgetType.Command => await commandRunner.ExecuteAsync(state.Configuration, cancellation.Token),
                _ => WidgetExecutionResult.Failure("Tipo de widget no válido.")
            };
            state.Result = result;
            if (result.Succeeded)
            {
                state.LastSuccessfulResult = result;
                state.LastError = null;
            }
            else state.LastError = result.Error;
            if (!result.Succeeded) logger.Error($"Widget '{state.Configuration.Id}' falló: {result.Error}");
            else logger.Info($"Widget '{state.Configuration.Id}' actualizado.");
        }
        catch (Exception exception)
        {
            state.Result = WidgetExecutionResult.Failure(exception.Message);
            logger.Error($"Widget '{state.Configuration.Id}' produjo un error aislado.", exception);
        }
        finally
        {
            state.Pending = false;
            if (screen is Screen.Widgets or Screen.Telemetry)
            {
                try { Render(); }
                catch (Exception exception) { logger.Error("No se pudo redibujar la TUI tras actualizar un widget.", exception); }
            }
        }
    }

    private void Render()
    {
        output.Write("\x1b[2J\x1b[H");
        output.WriteLine(configuration.Tui.Title);
        output.WriteLine(new string('=', Math.Min(GetWidth(), Math.Max(20, configuration.Tui.Title.Length))));
        switch (screen)
        {
            case Screen.Main: RenderMain(); break;
            case Screen.Info: RenderInfo(); break;
            case Screen.Api: RenderApi(); break;
            case Screen.Widgets: RenderWidgets(); break;
            case Screen.Telemetry: RenderTelemetry(); break;
            case Screen.Yazi: RenderYazi(); break;
            case Screen.Help: RenderHelp(); break;
        }
    }

    private void RenderMain()
    {
        output.WriteLine("Menú principal");
        RenderMenu(mainMenu);
        output.WriteLine();
        output.WriteLine("↑/↓ mover · Enter abrir · Esc salir");
    }

    private void RenderInfo()
    {
        output.WriteLine("Información");
        output.WriteLine($"TOML efectivo: {configurationPath}");
        if (configurationErrors.Count > 0)
        {
            output.WriteLine("Estado de configuración: inválido");
            foreach (var error in configurationErrors.Take(Math.Max(1, GetHeight() - 12))) output.WriteLine($"! {error}");
        }
        else
        {
            output.WriteLine("Estado de configuración: válido");
        }
        output.WriteLine();
        output.WriteLine("Hotkeys");
        output.WriteLine($"Terminal: {configuration.Hotkeys.Terminal}");
        output.WriteLine($"Archivos: {configuration.Hotkeys.Files}");
        output.WriteLine($"Launcher: {configuration.Hotkeys.Launcher}");
        output.WriteLine($"Navegador: {configuration.Hotkeys.Browser}");
        output.WriteLine($"Cerrar ventana: {configuration.Hotkeys.CloseWindow}");
        output.WriteLine($"Recuperación: {configuration.Hotkeys.Recovery}");
        output.WriteLine();
        output.WriteLine("Esc/Backspace volver · Q salir");
    }

    private void RenderWidgets()
    {
        output.WriteLine("Widgets");
        if (widgetMenu is null || widgetMenu.Items.Count == 0)
        {
            output.WriteLine("No hay widgets configurados.");
        }
        else
        {
            RenderMenu(widgetMenu);
            var selected = widgetMenu.SelectedItem;
            if (selected is not null && widgetStates.TryGetValue(selected.Id, out var state))
            {
                output.WriteLine();
                output.WriteLine($"{selected.Label}: {(state.Pending ? "actualizando..." : FormatResult(state.Result))}");
            }
        }
        output.WriteLine();
        output.WriteLine("↑/↓ mover · Enter/R actualizar · A actualizar todos · Esc volver");
    }

    private void StartTelemetryRefresh()
    {
        var scriptPath = telemetryScriptPathResolver();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            telemetryState.Result = WidgetExecutionResult.Failure($"No se encontró scripts\\{TelemetrySummaryScriptLocator.ScriptFileName} junto a la configuración o instalación.");
            telemetryState.LastError = telemetryState.Result.Error;
            return;
        }

        telemetryState.Configuration = new TuiWidgetConfiguration
        {
            Id = "telemetry",
            Title = "Telemetría",
            Type = TuiWidgetType.Command,
            Command = "powershell.exe",
            Arguments = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
            TimeoutMilliseconds = 60000,
            MaxOutputBytes = 16 * 1024,
            UpdateMilliseconds = 0
        };
        StartRefresh(telemetryState);
    }

    private void RenderTelemetry()
    {
        output.WriteLine("Telemetría de sesión");
        if (telemetryState.Pending) output.WriteLine("Actualizando resumen local...");
        else if (!string.IsNullOrWhiteSpace(telemetryState.LastError)) output.WriteLine($"Última actualización: error — {Truncate(telemetryState.LastError, widthProvider())}");
        else output.WriteLine("Última actualización completada.");

        var result = telemetryState.LastSuccessfulResult;
        if (result is null)
        {
            if (!telemetryState.Pending && telemetryState.Result is { Succeeded: false } failed)
            {
                output.WriteLine($"No hay resumen disponible: {Truncate(failed.Error ?? "error desconocido", widthProvider())}");
            }
            output.WriteLine();
            output.WriteLine("R actualizar · Esc/Backspace volver · Q salir");
            return;
        }

        var lines = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var resetViewport = !string.Equals(renderedTelemetryOutput, result.Output, StringComparison.Ordinal);
        renderedTelemetryOutput = result.Output;
        var visibleLines = Math.Max(1, GetHeight() - 7);
        telemetryViewport.SetItems(lines.Length, visibleLines, resetViewport);
        if (telemetryViewport.HasMoreAbove) output.WriteLine("↑ hay más líneas");
        foreach (var index in telemetryViewport.VisibleIndexes()) output.WriteLine(Truncate(lines[index], widthProvider()));
        if (telemetryViewport.HasMoreBelow) output.WriteLine("↓ hay más líneas");
        output.WriteLine();
        output.WriteLine("↑/↓ mover · PageUp/PageDown pantalla · Home/End · R actualizar · Esc/Backspace volver · Q salir");
    }

    private void EnterApi()
    {
        LeaveApi();
        screen = Screen.Api;
        apiSnapshot = null;
        apiError = null;
        if (!configuration.Api.Enabled) return;
        apiClient = apiClientFactory();
        apiClient.SnapshotReceived += OnApiSnapshot;
        apiClient.UpdateReceived += OnApiUpdate;
        apiClient.StateChanged += OnApiStateChanged;
        try
        {
            apiClient.ConnectAsync(cancellation.Token).GetAwaiter().GetResult();
            if (apiClient.State == LocalApiClientState.Connected)
            {
                apiSnapshot = apiClient.RequestSnapshotAsync(cancellation.Token).GetAwaiter().GetResult();
                apiClient.SubscribeAsync(cancellation.Token).GetAwaiter().GetResult();
                apiSnapshot ??= apiClient.Snapshot;
            }
            apiError = apiClient.Error;
        }
        catch (Exception exception)
        {
            apiError = exception.Message;
            logger.Error("La vista de API local no pudo conectarse.", exception);
        }
    }

    private void LeaveApi()
    {
        if (apiClient is null) return;
        apiClient.SnapshotReceived -= OnApiSnapshot;
        apiClient.UpdateReceived -= OnApiUpdate;
        apiClient.StateChanged -= OnApiStateChanged;
        try { apiClient.DisconnectAsync().GetAwaiter().GetResult(); } catch (Exception exception) { logger.Error("Falló el cierre de la API local en la TUI.", exception); }
        try { apiClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        apiClient = null;
    }

    private void OnApiSnapshot(object? sender, LocalApiSnapshotEventArgs args) => apiSnapshot = args.Snapshot;
    private void OnApiStateChanged(object? sender, LocalApiStateChangedEventArgs args)
    {
        apiError = args.Error;
        if (screen == Screen.Api)
        {
            try { Render(); } catch (Exception exception) { logger.Error("No se pudo redibujar el estado de API local.", exception); }
        }
    }
    private void OnApiUpdate(object? sender, LocalApiSnapshotEventArgs args)
    {
        apiSnapshot = args.Snapshot;
        if (screen == Screen.Api)
        {
            try { Render(); } catch (Exception exception) { logger.Error("No se pudo redibujar la vista de API local.", exception); }
        }
    }

    private void RenderApi()
    {
        output.WriteLine("API local de TenchyShell");
        var state = configuration.Api.Enabled ? apiClient?.State.ToString() ?? "Unavailable" : "Disabled";
        output.WriteLine($"Estado: {FormatApiState(state)}");
        output.WriteLine("Protocolo: v1");
        output.WriteLine($"Pipe: {LocalApiDefaults.PipeName}");
        output.WriteLine();
        output.WriteLine("Funciones expuestas");
        RenderApiGroup("Consultas", LocalApiCapabilityCatalog.Queries);
        RenderApiGroup("Comandos (no ejecutados desde esta vista)", LocalApiCapabilityCatalog.Commands);
        RenderApiGroup("Suscripciones", LocalApiCapabilityCatalog.Topics);
        output.WriteLine();
        if (!configuration.Api.Enabled)
        {
            output.WriteLine("La API está deshabilitada.");
            output.WriteLine("Añade [api] y enabled = true al TOML y reinicia TenchyShell.");
        }
        else if (apiSnapshot is not null)
        {
            output.WriteLine("Estado actual");
            output.WriteLine($"Workspace activo: {apiSnapshot.ActiveWorkspace}");
            output.WriteLine($"Workspaces: {apiSnapshot.Workspaces.Count}");
            output.WriteLine($"Aplicaciones: {apiSnapshot.Applications.Count}");
            output.WriteLine($"Ventanas: {apiSnapshot.Windows.Count}");
            RenderApiDetails(apiSnapshot);
        }
        else output.WriteLine($"API no disponible{(string.IsNullOrWhiteSpace(apiError) ? "." : $": {apiError}")}");
        output.WriteLine();
        output.WriteLine("Esc/Backspace volver · R reintentar conexión · Q salir");
    }

    private void RenderApiGroup(string title, IReadOnlyList<string> entries)
    {
        output.WriteLine(title);
        foreach (var entry in entries) output.WriteLine($"  {entry}");
    }

    private void RenderApiDetails(LocalApiSnapshot snapshot)
    {
        var available = Math.Max(1, GetHeight() - 20);
        foreach (var application in snapshot.Applications.Take(Math.Min(3, available))) output.WriteLine($"  App: {application.Name}");
        if (snapshot.Applications.Count > 3) output.WriteLine($"  … {snapshot.Applications.Count - 3} aplicaciones más");
        foreach (var window in snapshot.Windows.Take(Math.Min(3, Math.Max(0, available - 4)))) output.WriteLine($"  Ventana: {window.Title}");
        if (snapshot.Windows.Count > 3) output.WriteLine($"  … {snapshot.Windows.Count - 3} ventanas más");
    }

    private static string FormatApiState(string state) => state switch
    {
        nameof(LocalApiClientState.Connected) => "conectada",
        nameof(LocalApiClientState.Connecting) => "conectando",
        nameof(LocalApiClientState.Faulted) => "error",
        nameof(LocalApiClientState.Disabled) => "deshabilitada",
        _ => "no disponible"
    };

    private void RenderHelp()
    {
        output.WriteLine("Ayuda");
        output.WriteLine("La TUI se ejecuta como proceso separado dentro del terminal configurado.");
        output.WriteLine("No registra hotkeys ni crea ventanas Win32.");
        output.WriteLine("Los comandos de widgets sólo usan command y arguments declarados en TOML.");
        output.WriteLine("Esc/Backspace vuelve al menú anterior; Q sale desde una vista secundaria.");
    }

    private void RenderYazi()
    {
        output.WriteLine("Atajos de Yazi (solo lectura)");
        var result = yaziResult ?? LoadYazi(false);
        output.WriteLine($"Ruta: {result.Path.FilePath}");
        output.WriteLine($"Fuente: {(result.Source == YaziShortcutSource.DefaultPreset ? "preset integrado" : "configuración del usuario")}{(result.PresetVersion is null ? string.Empty : $" ({result.PresetVersion})")}");
        output.WriteLine($"Archivo: {(result.Path.Exists ? "existe" : "ausente")}");
        output.WriteLine($"Estado: {result.Status.ToString().ToLowerInvariant()}");
        // Two lines are emitted by Render() before this view. Keep two indicator
        // lines reserved so resizing or scrolling never pushes content past the viewport.
        var errorLines = Math.Min(result.Errors.Count, Math.Max(0, heightProvider() - 12));
        var available = Math.Max(0, heightProvider() - 11 - errorLines);
        yaziViewport.SetItems(result.Shortcuts.Count, available);
        output.WriteLine(yaziViewport.RangeText());
        if (yaziViewport.HasMoreAbove) output.WriteLine("↑ hay más atajos");
        foreach (var index in yaziViewport.VisibleIndexes())
        {
            var shortcut = result.Shortcuts[index];
            var marker = index == yaziViewport.SelectedIndex ? ">" : " ";
            output.WriteLine(TruncateShortcut($"{marker} {shortcut.Context}/{shortcut.Layer}  {shortcut.Sequence,-14}  {shortcut.Action}  {shortcut.Description}", shortcut, widthProvider()));
        }
        if (yaziViewport.HasMoreBelow) output.WriteLine("↓ hay más atajos");
        foreach (var error in result.Errors.Take(errorLines)) output.WriteLine(Truncate($"! {error}", widthProvider()));
        output.WriteLine("↑/↓ mover · PageUp/PageDown pantalla · Home/End · R recargar · Esc/Backspace volver · Q salir");
    }

    private YaziShortcutReadResult LoadYazi(bool reset)
    {
        try
        {
            yaziResult = yaziReader();
        }
        catch (Exception exception)
        {
            logger.Error("No se pudieron cargar los atajos de Yazi.", exception);
            yaziResult = new(new YaziShortcutPath(string.Empty, string.Empty, false), YaziShortcutLoadStatus.Invalid, Array.Empty<YaziShortcut>(), new[] { $"No se pudieron cargar los atajos de Yazi: {exception.Message}" });
        }
        yaziViewport.SetItems(yaziResult.Shortcuts.Count, 0, reset);
        return yaziResult;
    }

    private static string TruncateShortcut(string line, YaziShortcut shortcut, int width)
    {
        if (line.Length <= width || width <= 0) return line;
        var fixedPart = line[..Math.Max(0, line.Length - shortcut.Description.Length)];
        return Truncate(fixedPart + Truncate(shortcut.Description, Math.Max(0, width - fixedPart.Length)), width);
    }

    private static string Truncate(string value, int width)
    {
        if (width <= 0) return string.Empty;
        return value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";
    }

    private void RenderMenu(TuiMenu menu)
    {
        foreach (var (item, index) in menu.Items.Select((item, index) => (item, index)))
        {
            output.WriteLine($"{(index == menu.SelectedIndex ? ">" : " ")} {item.Label}");
        }
    }

    private static string FormatResult(WidgetExecutionResult? result)
    {
        if (result is null) return "sin actualizar (Enter para actualizar)";
        if (!result.Succeeded) return $"error: {result.Error}";
        return string.IsNullOrWhiteSpace(result.Output)
            ? "(sin salida)"
            : result.Output.TrimEnd() + (result.Truncated ? " …[truncado]" : string.Empty);
    }

    private void RenderExit()
    {
        output.Write("\x1b[2J\x1b[H");
        output.WriteLine("TenchyShell TUI finalizada. Regresando a la terminal.");
    }

    private ConsoleKey ReadKey()
    {
        if (input == Console.In && !Console.IsInputRedirected) return Console.ReadKey(intercept: true).Key;
        var value = bufferedInput >= 0 ? TakeBufferedInput() : input.Read();
        if (value < 0) return ConsoleKey.Escape;
        if (value == 27)
        {
            var next = input.Read();
            if (next != '[')
            {
                if (next >= 0) bufferedInput = next;
                return ConsoleKey.Escape;
            }
            var code = input.Read();
            return code switch
            {
                'A' => ConsoleKey.UpArrow,
                'B' => ConsoleKey.DownArrow,
                'H' => ConsoleKey.Home,
                'F' => ConsoleKey.End,
                '5' when input.Read() == '~' => ConsoleKey.PageUp,
                '6' when input.Read() == '~' => ConsoleKey.PageDown,
                '1' when input.Read() == '~' => ConsoleKey.Home,
                '4' when input.Read() == '~' => ConsoleKey.End,
                _ => ConsoleKey.Escape
            };
        }
        return value switch
        {
            27 => ConsoleKey.Escape,
            13 => ConsoleKey.Enter,
            _ => (ConsoleKey)char.ToUpperInvariant((char)value)
        };
    }

    private int TakeBufferedInput()
    {
        var value = bufferedInput;
        bufferedInput = -1;
        return value;
    }

    private static int GetWidth() { try { return Console.WindowWidth; } catch { return 80; } }

    private static int GetHeight() { try { return Console.WindowHeight; } catch { return 24; } }

    private static void TrySetCursorVisible(bool visible)
    {
        try { Console.CursorVisible = visible; } catch (IOException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    private enum Screen { Main, Info, Api, Widgets, Telemetry, Yazi, Help, Exit }

    private sealed class WidgetState
    {
        public WidgetState(TuiWidgetConfiguration configuration) => Configuration = configuration;
        public TuiWidgetConfiguration Configuration { get; set; }
        public WidgetExecutionResult? Result { get; set; }
        public WidgetExecutionResult? LastSuccessfulResult { get; set; }
        public string? LastError { get; set; }
        public bool Pending { get; set; }
    }
}
