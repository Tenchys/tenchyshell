using System.Diagnostics;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Network;
using TenchyShell.Core.Layout;
using TenchyShell.Core.Processes;
using TenchyShell.Core.Windows;
using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;
using TenchyShell.Win32;
using TenchyShell.Workspaces;

namespace TenchyShell.App;

internal sealed class ShellHost : IDisposable
{
    private const int RecoveryHotkeyId = 1;
    private const int LauncherHotkeyId = 2;
    private const int TerminalHotkeyId = 3;
    private const int FilesHotkeyId = 4;
    private const int BrowserHotkeyId = 5;
    private const int CloseWindowHotkeyId = 6;
    private const int WorkspaceSwitchHotkeyStart = 10;
    private const int WorkspaceMoveHotkeyStart = 20;
    private const int WindowMoveLeftHotkeyId = 30;
    private const int WindowMoveRightHotkeyId = 31;
    private const int WindowMoveUpHotkeyId = 32;
    private const int WindowMoveDownHotkeyId = 33;
    private const int WindowResizeGrowHotkeyId = 34;
    private const int WindowResizeShrinkHotkeyId = 35;
    private const int WindowMaximizeHotkeyId = 36;
    private const int WindowRestoreHotkeyId = 37;
    private const int WindowFocusHotkeyId = 38;
    private const int StatusPanelHotkeyId = 40;
    private const int LayoutZoneHotkeyStart = 50;
    private const int WindowSwitcherHotkeyId = 70;
    private const int SystemTrayHotkeyId = 71;
    private const int InputLanguageHotkeyId = 72;

    private readonly ShellConfiguration configuration;
    private readonly ILogger logger;
    private readonly MessageLoopHost messageLoop;
    private readonly ShellActions actions;
    private readonly WindowsApplicationCatalog applicationCatalog;
    private readonly ProcessApplicationLauncher applicationLauncher;
    private readonly LauncherWindow launcherWindow;
    private readonly IWindowService windowService;
    private readonly bool stopExplorerAfterHotkeys;
    private readonly WorkspaceManager workspaceManager;
    private readonly StatusPanelWindow? statusPanelWindow;
    private readonly LayoutZoneCatalog layoutZoneCatalog;
    private readonly LayoutInteractionHost? layoutInteractionHost;
    private readonly WindowSwitcherWindow? windowSwitcherWindow;
    private readonly SystemTrayService systemTrayService;
    private readonly SystemTrayScriptRunner systemTrayScriptRunner;
    private readonly WallpaperService wallpaperService;
    private readonly IWallpaperStateStore wallpaperStateStore;
    private bool isDisposed;

    public ShellHost(ShellConfiguration configuration, ILogger logger, bool stopExplorerAfterHotkeys = false)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.stopExplorerAfterHotkeys = stopExplorerAfterHotkeys;

        if (DpiAwareness.TryEnablePerMonitorV2(out var dpiError))
        {
            logger.Info("Conciencia DPI por monitor habilitada.");
        }
        else
        {
            logger.Error($"No se pudo habilitar la conciencia DPI por monitor: {dpiError}");
        }

        messageLoop = new MessageLoopHost();
        var processLauncher = new ProcessLauncher(logger);
        actions = new ShellActions(configuration, processLauncher, logger);
        applicationCatalog = new WindowsApplicationCatalog();
        applicationLauncher = new ProcessApplicationLauncher(processLauncher, logger);
        launcherWindow = new LauncherWindow(applicationCatalog);
        windowService = new WindowService();
        workspaceManager = new WorkspaceManager(new WorkspaceWindowService());
        statusPanelWindow = configuration.StatusPanel.Enabled
            ? new StatusPanelWindow(configuration.StatusPanel, logger)
            : null;
        layoutZoneCatalog = new LayoutZoneCatalog(configuration.Layout.Zones, configuration.Layout.MaxZones);
        layoutInteractionHost = configuration.Layout.Enabled
            ? new LayoutInteractionHost(
                messageLoop,
                windowService,
                layoutZoneCatalog,
                configuration.Layout.ZoneNumberSizePercent,
                logger)
            : null;
        windowSwitcherWindow = configuration.WindowSwitcher.Enabled
            ? new WindowSwitcherWindow(workspaceManager, new WorkspaceWindowService(), configuration.WindowSwitcher)
            : null;
        systemTrayScriptRunner = new SystemTrayScriptRunner();
        wallpaperStateStore = new WallpaperStateStore();
        wallpaperService = new WallpaperService(configuration.Wallpaper, wallpaperStateStore, logger);
        systemTrayService = new SystemTrayService(
            configuration,
            actions,
            systemTrayScriptRunner,
            new NetworkService(),
            wallpaperService,
            new InputLanguageService(),
            logger,
            messageLoop);
    }

    public ShellActions Actions => actions;

    public int Run(int? exitAfterSeconds = null)
    {
        Timer? automaticStopTimer = null;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Console.CancelKeyPress += OnCancelKeyPress;
        messageLoop.FatalError += OnFatalError;
        messageLoop.HotkeyPressed += OnHotkeyPressed;
        messageLoop.HotkeyRegistered += OnHotkeyRegistered;
        messageLoop.HotkeyRegistrationFailed += OnHotkeyRegistrationFailed;
        launcherWindow.ApplicationSelected += OnApplicationSelected;
        launcherWindow.CommandRequested += OnCommandRequested;
        if (statusPanelWindow is not null)
        {
            statusPanelWindow.TrayRequested += OpenSystemTray;
        }

        try
        {
            ConfigureHotkeys();
            workspaceManager.Refresh();
            statusPanelWindow?.SetWorkspace(workspaceManager.CurrentWorkspace);
            statusPanelWindow?.Start();
            applicationCatalog.Refresh();
            var monitors = new MonitorService().GetAll();
            logger.Info($"Monitores disponibles para layout: {monitors.Count}.");
            logger.Info($"Catálogo de aplicaciones cargado: {applicationCatalog.GetAll().Count} entradas.");
            logger.Info($"TenchyShell iniciado. Terminal configurado: {configuration.Terminal.Command}.");
            logger.Info(stopExplorerAfterHotkeys
                ? "Modo de prueba sin Explorer solicitado."
                : "Modo de desarrollo con Explorer disponible.");
            Console.WriteLine("TenchyShell iniciado. Presiona Ctrl+C para salir durante el desarrollo.");
            Console.WriteLine($"Recuperación: {configuration.Hotkeys.Recovery} inicia explorer.exe.");
            return messageLoop.Run(() =>
            {
                if (stopExplorerAfterHotkeys)
                {
                    StopExplorerAfterHotkeys();
                }

                layoutInteractionHost?.Start();
                RestoreWallpaperOnStartup();
                if (exitAfterSeconds.HasValue)
                {
                    automaticStopTimer = new Timer(
                        _ => messageLoop.Stop(),
                        null,
                        TimeSpan.FromSeconds(exitAfterSeconds.Value),
                        Timeout.InfiniteTimeSpan);
                    logger.Info($"Cierre automático configurado en {exitAfterSeconds.Value} segundos.");
                }
            });
        }
        catch (Exception exception)
        {
            OnFatalError(exception);
            return 1;
        }
        finally
        {
            automaticStopTimer?.Dispose();
            messageLoop.FatalError -= OnFatalError;
            messageLoop.HotkeyPressed -= OnHotkeyPressed;
            messageLoop.HotkeyRegistered -= OnHotkeyRegistered;
            messageLoop.HotkeyRegistrationFailed -= OnHotkeyRegistrationFailed;
            launcherWindow.ApplicationSelected -= OnApplicationSelected;
            launcherWindow.CommandRequested -= OnCommandRequested;
            if (statusPanelWindow is not null)
            {
                statusPanelWindow.TrayRequested -= OpenSystemTray;
            }
            Console.CancelKeyPress -= OnCancelKeyPress;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            Dispose();
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        layoutInteractionHost?.Dispose();
        systemTrayService.Dispose();
        wallpaperService.Dispose();
        windowSwitcherWindow?.Dispose();
        statusPanelWindow?.Dispose();
        messageLoop.Dispose();
        launcherWindow.Dispose();
        logger.Info("TenchyShell finalizado; se liberaron hotkeys y recursos Win32.");
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnRecoveryRequested()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });
            logger.Info("Se inició explorer.exe mediante el hotkey de recuperación.");
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo iniciar explorer.exe mediante el hotkey de recuperación.", exception);
            Console.Error.WriteLine($"No se pudo iniciar explorer.exe: {exception.Message}");
        }
    }

    private void RestoreWallpaperOnStartup()
    {
        if (!configuration.Wallpaper.Enabled) return;
        _ = RestoreWallpaperOnStartupAsync();
    }

    private async Task RestoreWallpaperOnStartupAsync()
    {
        WallpaperStateLoadResult state;
        try
        {
            state = await Task.Run(wallpaperStateStore.Load).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            messageLoop.Post(() => logger.Error("No se pudo cargar el estado del wallpaper al iniciar.", exception));
            return;
        }

        messageLoop.Post(() =>
        {
            if (isDisposed || !state.HasSavedWallpaper)
            {
                if (!string.IsNullOrWhiteSpace(state.Error)) logger.Error(state.Error);
                return;
            }

            if (!File.Exists(state.LastWallpaperPath))
            {
                logger.Info($"El último wallpaper ya no existe: {state.LastWallpaperPath}.");
                return;
            }

            var result = wallpaperService.Apply(state.LastWallpaperPath);
            if (result.Succeeded)
            {
                logger.Info($"Wallpaper restaurado al iniciar: {state.LastWallpaperPath}.");
            }
            else
            {
                logger.Error(result.Error ?? "No se pudo restaurar el último wallpaper.");
            }
        });
    }

    private void StopExplorerAfterHotkeys()
    {
        const int maximumPasses = 3;
        for (var pass = 1; pass <= maximumPasses; pass++)
        {
            var explorers = GetCurrentSessionExplorerProcesses();
            if (explorers.Length == 0)
            {
                Thread.Sleep(1500);
                explorers = GetCurrentSessionExplorerProcesses();
                if (explorers.Length == 0)
                {
                    logger.Info($"El modo sin Explorer quedó estable tras {pass - 1} pasada(s) de cierre.");
                    return;
                }
            }

            logger.Info(pass == 1
                ? $"El modo sin Explorer cerrará {explorers.Length} proceso(s) explorer.exe tras registrar el hotkey de recuperación."
                : $"Windows relanzó explorer.exe; pasada de cierre adicional {pass}/{maximumPasses} ({explorers.Length} proceso(s)).");

            foreach (var explorer in explorers)
            {
                try
                {
                    if (explorer.CloseMainWindow())
                    {
                        explorer.WaitForExit(3000);
                    }

                    if (!explorer.HasExited)
                    {
                        explorer.Kill();
                        explorer.WaitForExit(3000);
                    }

                    if (explorer.HasExited)
                    {
                        logger.Info($"Se cerró explorer.exe (PID: {explorer.Id}).");
                    }
                    else
                    {
                        logger.Error($"explorer.exe (PID: {explorer.Id}) no terminó dentro del tiempo de espera.");
                    }
                }
                catch (Exception exception)
                {
                    logger.Error($"No se pudo cerrar explorer.exe (PID: {explorer.Id}).", exception);
                    Console.Error.WriteLine($"No se pudo cerrar explorer.exe (PID: {explorer.Id}): {exception.Message}");
                }
                finally
                {
                    explorer.Dispose();
                }
            }

            Thread.Sleep(500);
        }

        Thread.Sleep(1500);
        var remainingExplorers = GetCurrentSessionExplorerProcesses();
        if (remainingExplorers.Length > 0)
        {
            foreach (var explorer in remainingExplorers) explorer.Dispose();
            throw new InvalidOperationException(
                "Windows continuó relanzando explorer.exe después de tres pasadas; se canceló el modo sin Explorer.");
        }

        logger.Info("El modo sin Explorer quedó estable después de la última pasada de cierre.");
    }

    private static Process[] GetCurrentSessionExplorerProcesses()
    {
        using var current = Process.GetCurrentProcess();
        var sessionId = current.SessionId;
        var allExplorers = Process.GetProcessesByName("explorer");
        var matching = new List<Process>();
        foreach (var explorer in allExplorers)
        {
            try
            {
                if (explorer.SessionId == sessionId) matching.Add(explorer);
                else explorer.Dispose();
            }
            catch
            {
                explorer.Dispose();
            }
        }
        return matching.ToArray();
    }

    private void ConfigureHotkeys()
    {
        ConfigureHotkey(RecoveryHotkeyId, "recuperación", configuration.Hotkeys.Recovery, required: true);
        ConfigureHotkey(TerminalHotkeyId, "terminal", configuration.Hotkeys.Terminal);
        ConfigureHotkey(FilesHotkeyId, "archivos", configuration.Hotkeys.Files);
        ConfigureHotkey(BrowserHotkeyId, "navegador", configuration.Hotkeys.Browser);
        ConfigureHotkey(CloseWindowHotkeyId, "cerrar ventana", configuration.Hotkeys.CloseWindow);

        for (var workspace = WorkspaceManager.FirstWorkspace; workspace <= WorkspaceManager.LastWorkspace; workspace++)
        {
            ConfigureHotkey(
                WorkspaceSwitchHotkeyStart + workspace - 1,
                $"cambiar al workspace {workspace}",
                configuration.WorkspaceHotkeys.Switch[workspace - 1]);
            ConfigureHotkey(
                WorkspaceMoveHotkeyStart + workspace - 1,
                $"mover al workspace {workspace}",
                configuration.WorkspaceHotkeys.Move[workspace - 1]);
        }

        ConfigureHotkey(WindowMoveLeftHotkeyId, "mover ventana a la izquierda", configuration.WindowHotkeys.MoveLeft);
        ConfigureHotkey(WindowMoveRightHotkeyId, "mover ventana a la derecha", configuration.WindowHotkeys.MoveRight);
        ConfigureHotkey(WindowMoveUpHotkeyId, "mover ventana arriba", configuration.WindowHotkeys.MoveUp);
        ConfigureHotkey(WindowMoveDownHotkeyId, "mover ventana abajo", configuration.WindowHotkeys.MoveDown);
        ConfigureHotkey(WindowResizeGrowHotkeyId, "aumentar ventana", configuration.WindowHotkeys.ResizeGrow);
        ConfigureHotkey(WindowResizeShrinkHotkeyId, "reducir ventana", configuration.WindowHotkeys.ResizeShrink);
        ConfigureHotkey(WindowMaximizeHotkeyId, "maximizar ventana", configuration.WindowHotkeys.Maximize);
        ConfigureHotkey(WindowRestoreHotkeyId, "restaurar ventana", configuration.WindowHotkeys.Restore);
        ConfigureHotkey(WindowFocusHotkeyId, "enfocar ventana", configuration.WindowHotkeys.Focus);

        if (configuration.StatusPanel.Enabled)
        {
            ConfigureHotkey(StatusPanelHotkeyId, "panel informativo", configuration.StatusPanel.Hotkey);
        }

        if (configuration.WindowSwitcher.Enabled)
        {
            if (string.Equals(configuration.WindowSwitcher.Hotkey.Trim(), "Tab", StringComparison.OrdinalIgnoreCase))
            {
                logger.Error("El selector de ventanas no registrará 'Tab' sin modificadores para preservar el Alt+Tab nativo. Usa Ctrl+Alt+Tab.");
            }
            else
            {
                ConfigureHotkey(WindowSwitcherHotkeyId, "selector de ventanas", configuration.WindowSwitcher.Hotkey);
            }
        }

        if (configuration.SystemTray.Enabled)
        {
            ConfigureHotkey(SystemTrayHotkeyId, "bandeja del sistema", configuration.SystemTray.Hotkey);
        }

        if (configuration.InputLanguage.Enabled && !string.IsNullOrWhiteSpace(configuration.InputLanguage.Hotkey))
        {
            ConfigureHotkey(InputLanguageHotkeyId, "selector de idioma", configuration.InputLanguage.Hotkey);
        }

        if (configuration.Launcher.Enabled)
        {
            ConfigureHotkey(LauncherHotkeyId, "launcher", configuration.Hotkeys.Launcher);
        }

        if (configuration.Layout.Enabled)
        {
            for (var zone = 1; zone <= 9; zone++)
            {
                ConfigureOptionalHotkey(
                    LayoutZoneHotkeyStart + zone - 1,
                    $"layout de la zona {zone}",
                    configuration.LayoutHotkeys.Zones[zone - 1]);
            }
        }
    }

    private void ConfigureHotkey(int id, string actionName, string configuredValue, bool required = false)
    {
        if (!HotkeyParser.TryParse(configuredValue, out var combination, out var error))
        {
            throw new InvalidOperationException($"El hotkey de {actionName} no es válido: {error}");
        }

        try
        {
            messageLoop.ConfigureHotkey(id, combination, required);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"El hotkey de {actionName} ('{configuredValue}') entra en conflicto con otra acción configurada.",
                exception);
        }
    }

    private void ConfigureOptionalHotkey(int id, string actionName, string configuredValue)
    {
        try
        {
            ConfigureHotkey(id, actionName, configuredValue);
        }
        catch (InvalidOperationException exception)
        {
            var message = $"No se pudo configurar el hotkey opcional de {actionName} ('{configuredValue}'): {exception.Message}";
            logger.Error(message, exception);
            Console.Error.WriteLine(message);
        }
    }

    private void OnHotkeyPressed(int id)
    {
        logger.Info($"Hotkey recibido: {id}.");

        switch (id)
        {
            case RecoveryHotkeyId:
                OnRecoveryRequested();
                break;
            case LauncherHotkeyId when configuration.Launcher.Enabled:
                launcherWindow.Toggle();
                logger.Info(launcherWindow.IsVisible ? "Launcher mostrado." : "Launcher ocultado.");
                break;
            case TerminalHotkeyId:
                ReportLaunchFailure("terminal", actions.LaunchTerminal());
                break;
            case FilesHotkeyId:
                ReportLaunchFailure("Yazi", actions.LaunchFiles());
                break;
            case BrowserHotkeyId:
                ReportLaunchFailure("navegador", actions.LaunchBrowser());
                break;
            case CloseWindowHotkeyId:
                CloseActiveWindow();
                break;
            case >= WorkspaceSwitchHotkeyStart and < WorkspaceSwitchHotkeyStart + 9:
                SwitchWorkspace(id - WorkspaceSwitchHotkeyStart + 1);
                break;
            case >= WorkspaceMoveHotkeyStart and < WorkspaceMoveHotkeyStart + 9:
                MoveForegroundToWorkspace(id - WorkspaceMoveHotkeyStart + 1);
                break;
            case WindowMoveLeftHotkeyId:
                ReportWindowOperation(windowService.MoveActiveWindow(-40, 0));
                break;
            case WindowMoveRightHotkeyId:
                ReportWindowOperation(windowService.MoveActiveWindow(40, 0));
                break;
            case WindowMoveUpHotkeyId:
                ReportWindowOperation(windowService.MoveActiveWindow(0, -40));
                break;
            case WindowMoveDownHotkeyId:
                ReportWindowOperation(windowService.MoveActiveWindow(0, 40));
                break;
            case WindowResizeGrowHotkeyId:
                ReportWindowOperation(windowService.ResizeActiveWindow(80, 50));
                break;
            case WindowResizeShrinkHotkeyId:
                ReportWindowOperation(windowService.ResizeActiveWindow(-80, -50));
                break;
            case WindowMaximizeHotkeyId:
                ReportWindowOperation(windowService.MaximizeActiveWindow());
                break;
            case WindowRestoreHotkeyId:
                ReportWindowOperation(windowService.RestoreActiveWindow());
                break;
            case WindowFocusHotkeyId:
                ReportWindowOperation(windowService.FocusActiveWindow());
                break;
            case StatusPanelHotkeyId when configuration.StatusPanel.Enabled:
                statusPanelWindow?.ToggleByHotkey();
                logger.Info(statusPanelWindow?.IsVisible == true
                    ? "Panel informativo mostrado mediante hotkey."
                    : "Panel informativo ocultado mediante hotkey.");
                break;
            case WindowSwitcherHotkeyId when configuration.WindowSwitcher.Enabled:
                if (KeyboardState.IsAltPressed)
                {
                    // Nunca sustituir el Alt+Tab nativo, incluso con una configuración antigua.
                    break;
                }
                windowSwitcherWindow?.Toggle();
                logger.Info(windowSwitcherWindow?.IsVisible == true
                    ? "Selector de ventanas mostrado."
                    : "Selector de ventanas ocultado.");
                break;
            case SystemTrayHotkeyId when configuration.SystemTray.Enabled:
                OpenSystemTray();
                break;
            case InputLanguageHotkeyId when configuration.InputLanguage.Enabled:
                OpenInputLanguageSelector();
                break;
            case >= LayoutZoneHotkeyStart and < LayoutZoneHotkeyStart + 9 when configuration.Layout.Enabled:
                PlaceActiveWindowInZone(id - LayoutZoneHotkeyStart + 1);
                break;
        }
    }

    private void OnHotkeyRegistered(int id)
    {
        logger.Info($"Hotkey registrado: {id}.");
    }

    private void OnHotkeyRegistrationFailed(int id, int errorCode)
    {
        var (actionName, configuredValue) = GetHotkeyDescription(id);
        var message = $"No se pudo registrar el hotkey de {actionName} ('{configuredValue}'). Código Win32: {errorCode}.";
        logger.Error(message);
        Console.Error.WriteLine(message);
    }

    private (string ActionName, string ConfiguredValue) GetHotkeyDescription(int id) => id switch
    {
        LauncherHotkeyId => ("launcher", configuration.Hotkeys.Launcher),
        WindowSwitcherHotkeyId => ("selector de ventanas", configuration.WindowSwitcher.Hotkey),
        SystemTrayHotkeyId => ("bandeja del sistema", configuration.SystemTray.Hotkey),
        InputLanguageHotkeyId => ("selector de idioma", configuration.InputLanguage.Hotkey),
        StatusPanelHotkeyId => ("panel informativo", configuration.StatusPanel.Hotkey),
        TerminalHotkeyId => ("terminal", configuration.Hotkeys.Terminal),
        FilesHotkeyId => ("archivos", configuration.Hotkeys.Files),
        BrowserHotkeyId => ("navegador", configuration.Hotkeys.Browser),
        CloseWindowHotkeyId => ("cerrar ventana", configuration.Hotkeys.CloseWindow),
        >= WorkspaceSwitchHotkeyStart and < WorkspaceSwitchHotkeyStart + 9 => (
            $"cambiar al workspace {id - WorkspaceSwitchHotkeyStart + 1}",
            configuration.WorkspaceHotkeys.Switch[id - WorkspaceSwitchHotkeyStart]),
        >= WorkspaceMoveHotkeyStart and < WorkspaceMoveHotkeyStart + 9 => (
            $"mover al workspace {id - WorkspaceMoveHotkeyStart + 1}",
            configuration.WorkspaceHotkeys.Move[id - WorkspaceMoveHotkeyStart]),
        >= LayoutZoneHotkeyStart and < LayoutZoneHotkeyStart + 9 => (
            $"layout de la zona {id - LayoutZoneHotkeyStart + 1}",
            configuration.LayoutHotkeys.Zones[id - LayoutZoneHotkeyStart]),
        WindowMoveLeftHotkeyId => ("mover ventana a la izquierda", configuration.WindowHotkeys.MoveLeft),
        WindowMoveRightHotkeyId => ("mover ventana a la derecha", configuration.WindowHotkeys.MoveRight),
        WindowMoveUpHotkeyId => ("mover ventana arriba", configuration.WindowHotkeys.MoveUp),
        WindowMoveDownHotkeyId => ("mover ventana abajo", configuration.WindowHotkeys.MoveDown),
        WindowResizeGrowHotkeyId => ("aumentar ventana", configuration.WindowHotkeys.ResizeGrow),
        WindowResizeShrinkHotkeyId => ("reducir ventana", configuration.WindowHotkeys.ResizeShrink),
        WindowMaximizeHotkeyId => ("maximizar ventana", configuration.WindowHotkeys.Maximize),
        WindowRestoreHotkeyId => ("restaurar ventana", configuration.WindowHotkeys.Restore),
        WindowFocusHotkeyId => ("enfocar ventana", configuration.WindowHotkeys.Focus),
        _ => ($"identificador {id}", "desconocido")
    };

    private void SwitchWorkspace(int workspace)
    {
        var result = workspaceManager.SwitchTo(workspace);
        if (result.Succeeded)
        {
            statusPanelWindow?.SetWorkspace(workspace);
            logger.Info($"Workspace activo: {workspace}.");
        }
        else
        {
            logger.Error($"No se pudo cambiar al workspace {workspace}: {result.Error}");
        }
    }

    private void MoveForegroundToWorkspace(int workspace)
    {
        var result = workspaceManager.MoveForegroundTo(workspace);
        if (result.Succeeded)
        {
            logger.Info($"Ventana activa movida al workspace {workspace}.");
        }
        else
        {
            logger.Error($"No se pudo mover la ventana activa al workspace {workspace}: {result.Error}");
        }
    }

    private void PlaceActiveWindowInZone(int zoneNumber)
    {
        if (!windowService.TryGetActiveMonitor(out var monitor, out var error))
        {
            logger.Error($"No se pudo obtener el monitor para la zona {zoneNumber}: {error}");
            Console.Error.WriteLine($"No se pudo colocar la ventana en la zona {zoneNumber}: {error}");
            return;
        }

        if (!layoutZoneCatalog.TryGetZone(monitor.Id, monitor.IsPrimary, zoneNumber, out var zone))
        {
            logger.Error($"No existe una zona de layout numerada {zoneNumber} para el monitor '{monitor.Id}'.");
            Console.Error.WriteLine($"No existe una zona de layout numerada {zoneNumber} para el monitor '{monitor.Id}'.");
            return;
        }

        var targetRect = LayoutZoneCalculator.ToWindowRect(zone, monitor.WorkArea);
        ReportWindowOperation(windowService.PlaceActiveWindow(targetRect));
    }

    private void OpenSystemTray()
    {
        if (systemTrayService.TryOpen(out var error))
        {
            logger.Info("Se solicitó acceso a la bandeja del sistema.");
            return;
        }

        logger.Error($"No se pudo abrir la bandeja del sistema: {error}");
        Console.Error.WriteLine($"No se pudo abrir la bandeja del sistema: {error}");
    }

    private void OpenInputLanguageSelector()
    {
        if (systemTrayService.TryOpenInputLanguageSelector(out var error))
        {
            logger.Info("Se solicitó acceso al selector de idioma.");
            return;
        }

        logger.Error($"No se pudo abrir el selector de idioma: {error}");
        Console.Error.WriteLine($"No se pudo abrir el selector de idioma: {error}");
    }

    private void ReportWindowOperation(WindowOperationResult result)
    {
        if (!result.Succeeded)
        {
            logger.Error($"No se pudo operar sobre la ventana activa: {result.Error}");
            Console.Error.WriteLine($"No se pudo operar sobre la ventana activa: {result.Error}");
        }
    }

    private void OnApplicationSelected(ApplicationEntry application)
    {
        var result = applicationLauncher.Launch(application);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"No se pudo iniciar '{application.DisplayName}': {result.Error}");
        }
    }

    private void CloseActiveWindow()
    {
        var result = windowService.CloseActiveWindow();

        if (result.Succeeded)
        {
            logger.Info("Se solicitó el cierre de la ventana activa mediante WM_CLOSE.");
            return;
        }

        logger.Error($"No se pudo cerrar la ventana activa: {result.Error}");
        Console.Error.WriteLine($"No se pudo cerrar la ventana activa: {result.Error}");
    }

    private void OnCommandRequested(string command)
    {
        var result = actions.LaunchCommand(command);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"No se pudo ejecutar el comando: {result.Error}");
        }
    }

    private static void ReportLaunchFailure(string actionName, ProcessLaunchResult result)
    {
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"No se pudo iniciar {actionName}: {result.Error}");
        }
    }

    private void OnFatalError(Exception exception)
    {
        logger.Error("Error no controlado en TenchyShell.", exception);
        Console.Error.WriteLine($"Error no controlado: {exception.Message}");
        messageLoop.Stop();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            logger.Error("Excepción no controlada del proceso.", exception);
        }
        else
        {
            logger.Error($"Excepción no controlada del proceso: {args.ExceptionObject}.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        logger.Error("Excepción no observada de una tarea.", args.Exception);
        args.SetObserved();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        logger.Info("Cierre solicitado mediante Ctrl+C.");
        messageLoop.Stop();
    }
}
