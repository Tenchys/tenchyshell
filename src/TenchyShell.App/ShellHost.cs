using System.Diagnostics;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Network;
using TenchyShell.Core.Notifications;
using TenchyShell.Core.Layout;
using TenchyShell.Core.Processes;
using TenchyShell.Core.Session;
using TenchyShell.Core.Windows;
using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;
using TenchyShell.Win32;
using TenchyShell.Workspaces;
using TenchyShell.Tui;

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
    private readonly OptionalComponentsSelection optionalComponents;
    private readonly ILogger logger;
    private readonly MessageLoopHost messageLoop;
    private readonly ShellActions actions;
    private readonly SessionActionService sessionActions;
    private readonly SessionConfirmationStateMachine sessionConfirmation = new();
    private readonly WindowsApplicationCatalog? applicationCatalog;
    private readonly ProcessApplicationLauncher? applicationLauncher;
    private readonly LauncherWindow? launcherWindow;
    private readonly IWindowService windowService;
    private readonly bool stopExplorerAfterHotkeys;
    private readonly bool startTui;
    private readonly string effectiveConfigurationPath;
    private readonly WorkspaceManager workspaceManager;
    private readonly IWorkspaceWindowService workspaceWindowService;
    private readonly IMinimizedWindowService? minimizedWindowService;
    private readonly MinimizedWindowVisibilityService? minimizedWindowVisibilityService;
    private readonly StatusPanelWindow? statusPanelWindow;
    private readonly LayoutZoneCatalog layoutZoneCatalog;
    private readonly LayoutInteractionHost? layoutInteractionHost;
    private readonly WindowSwitcherWindow? windowSwitcherWindow;
    private readonly WindowSwitcherAltTabHook? windowSwitcherAltTabHook;
    private readonly SystemTrayService systemTrayService;
    private readonly SystemTrayScriptRunner systemTrayScriptRunner;
    private readonly WallpaperService wallpaperService;
    private readonly IWallpaperStateStore wallpaperStateStore;
    private readonly ExplorerShellController explorerShellController;
    private readonly DesktopAreaPolicy desktopAreaPolicy;
    private readonly LiveBenchmarkRecorder benchmarkRecorder;
    private readonly SessionStabilityMetrics stabilityMetrics;
    private readonly NotificationCenter notificationCenter;
    private readonly NotificationBridgeClient? notificationBridgeClient;
    private readonly NotificationWindow? notificationWindow;
    private readonly WindowsApplicationCatalog? localApiApplicationCatalog;
    private readonly LocalApiStateProvider? localApiStateProvider;
    private readonly LocalApiServer? localApiServer;
    private readonly LocalApiWindowChangeMonitor? localApiWindowMonitor;
    private readonly LocalApiApplicationWatcher? localApiApplicationWatcher;
    private bool isDisposed;

    public ShellHost(
        ShellConfiguration configuration,
        ILogger logger,
        bool stopExplorerAfterHotkeys = false,
        bool startTui = true,
        string? effectiveConfigurationPath = null,
        bool suppressLiveBenchmark = false)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.stopExplorerAfterHotkeys = stopExplorerAfterHotkeys;
        this.startTui = startTui;
        this.effectiveConfigurationPath = effectiveConfigurationPath ?? configuration.ConfigurationPath;
        optionalComponents = OptionalComponentsSelection.From(configuration);
        LogOptionalComponentState();

        if (DpiAwareness.TryEnablePerMonitorV2(out var dpiError))
        {
            logger.Info("Conciencia DPI por monitor habilitada.");
        }
        else
        {
            logger.Error($"No se pudo habilitar la conciencia DPI por monitor: {dpiError}");
        }

        messageLoop = new MessageLoopHost();
        stabilityMetrics = new SessionStabilityMetrics();
        var processLauncher = new ProcessLauncher(logger);
        actions = new ShellActions(configuration, processLauncher, logger);
        sessionActions = new SessionActionService(processLauncher, logger);
        applicationCatalog = optionalComponents.ApplicationSearchEnabled
            ? new WindowsApplicationCatalog()
            : null;
        applicationLauncher = optionalComponents.ApplicationSearchEnabled
            ? new ProcessApplicationLauncher(
                processLauncher,
                new WindowsPackagedApplicationActivator(),
                logger,
                stabilityMetrics)
            : null;
        launcherWindow = optionalComponents.ApplicationSearchEnabled
            ? new LauncherWindow(applicationCatalog!)
            : null;
        desktopAreaPolicy = new DesktopAreaPolicy();
        benchmarkRecorder = new LiveBenchmarkRecorder(
            configuration.Benchmark,
            logger,
            suppressed: suppressLiveBenchmark);
        notificationCenter = new NotificationCenter();
        notificationBridgeClient = configuration.Notifications.Enabled ? new NotificationBridgeClient(logger) : null;
        notificationWindow = new NotificationWindow(notificationCenter, logger, messageLoop, desktopAreaPolicy);
        if (applicationLauncher is not null)
        {
            applicationLauncher.PackagedApplicationActivationFailed += OnPackagedApplicationActivationFailed;
        }
        if (notificationBridgeClient is not null)
        {
            notificationBridgeClient.NotificationReceived += OnNotificationReceived;
            notificationBridgeClient.NotificationRemoved += notificationCenter.Remove;
            notificationCenter.DismissRequested += notificationBridgeClient.Dismiss;
        }
        windowService = new WindowService(desktopAreaPolicy: desktopAreaPolicy);
        workspaceWindowService = new WorkspaceWindowService(benchmarkRecorder);
        workspaceManager = new WorkspaceManager(workspaceWindowService);
        if (configuration.Api.Enabled)
        {
            localApiApplicationCatalog = new WindowsApplicationCatalog();
            localApiStateProvider = new LocalApiStateProvider(
                messageLoop,
                workspaceManager,
                workspaceWindowService,
                localApiApplicationCatalog);
            localApiServer = new LocalApiServer(localApiStateProvider, logger);
            localApiWindowMonitor = new LocalApiWindowChangeMonitor(messageLoop, localApiStateProvider.RefreshWindows, logger);
            localApiApplicationWatcher = new LocalApiApplicationWatcher(messageLoop, localApiStateProvider, logger);
        }
        else
        {
            logger.Info("API local deshabilitada; no se crearán Named Pipe, hooks ni watchers.");
        }
        statusPanelWindow = optionalComponents.DockEnabled
            ? new StatusPanelWindow(configuration.StatusPanel, logger, desktopAreaPolicy, configuration.Dock)
            : null;
        minimizedWindowVisibilityService = optionalComponents.DockEnabled
            ? new MinimizedWindowVisibilityService(
                messageLoop,
                workspaceManager,
                workspaceWindowService,
                logger)
            : null;
        minimizedWindowService = optionalComponents.DockEnabled
            ? new MinimizedWindowService(
                workspaceManager,
                workspaceWindowService,
                minimizedWindowVisibilityService)
            : null;
        layoutZoneCatalog = new LayoutZoneCatalog(configuration.Layout.Zones, configuration.Layout.MaxZones);
        layoutInteractionHost = configuration.Layout.Enabled
            ? new LayoutInteractionHost(
                messageLoop,
                windowService,
                layoutZoneCatalog,
                configuration.Layout.ZoneNumberSizePercent,
                logger,
                desktopAreaPolicy)
            : null;
        windowSwitcherWindow = configuration.WindowSwitcher.Enabled
            ? new WindowSwitcherWindow(workspaceManager, workspaceWindowService, configuration.WindowSwitcher, logger, benchmarkRecorder, stabilityMetrics)
            : null;
        windowSwitcherAltTabHook = configuration.WindowSwitcher.Enabled && IsAltTabHotkey(configuration.WindowSwitcher.Hotkey)
            ? new WindowSwitcherAltTabHook(messageLoop, windowSwitcherWindow!, logger)
            : null;
        systemTrayScriptRunner = new SystemTrayScriptRunner();
        wallpaperStateStore = new WallpaperStateStore();
        explorerShellController = new ExplorerShellController();
        wallpaperService = new WallpaperService(configuration.Wallpaper, wallpaperStateStore, logger);
        systemTrayService = new SystemTrayService(
            configuration,
            actions,
            systemTrayScriptRunner,
            new NetworkService(),
            wallpaperService,
            new InputLanguageService(),
            logger,
            messageLoop,
            desktopAreaPolicy,
            benchmarkRecorder,
            configuration.Notifications.Enabled ? notificationCenter : null,
            minimizedWindowService);
    }

    public ShellActions Actions => actions;

    public int Run(
        int? exitAfterSeconds = null,
        bool showBenchmarkTestNotification = false,
        PackagedActivationDiagnosticOptions? packagedDiagnostic = null)
    {
        Timer? automaticStopTimer = null;
        using var diagnosticCancellation = new CancellationTokenSource();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Console.CancelKeyPress += OnCancelKeyPress;
        messageLoop.FatalError += OnFatalError;
        messageLoop.HotkeyPressed += OnHotkeyPressed;
        messageLoop.HotkeyRegistered += OnHotkeyRegistered;
        messageLoop.HotkeyRegistrationFailed += OnHotkeyRegistrationFailed;
        if (launcherWindow is not null)
        {
            launcherWindow.ApplicationSelected += OnApplicationSelected;
            launcherWindow.CommandRequested += OnCommandRequested;
        }
        if (statusPanelWindow is not null)
        {
            statusPanelWindow.TrayRequested += OpenSystemTrayFromPointer;
            statusPanelWindow.DockItemRequested += OnDockItemRequested;
            statusPanelWindow.Closed += CancelSessionConfirmation;
            systemTrayService.VisibilityChanged += OnSystemTrayVisibilityChanged;
        }

        try
        {
            ConfigureHotkeys();
            windowSwitcherAltTabHook?.Start();
            workspaceManager.Refresh();
            if (localApiApplicationCatalog is WindowsApplicationCatalog apiCatalog)
            {
                apiCatalog.Refresh();
            }
            minimizedWindowVisibilityService?.Start();
            statusPanelWindow?.SetWorkspace(workspaceManager.CurrentWorkspace);
            statusPanelWindow?.Start();
            if (applicationCatalog is not null)
            {
                applicationCatalog.Refresh();
                logger.Info($"Catálogo de aplicaciones cargado: {applicationCatalog.GetAll().Count} entradas.");
            }
            var monitors = new MonitorService().GetAll();
            logger.Info($"Monitores disponibles para layout: {monitors.Count}.");
            logger.Info($"TenchyShell iniciado. Terminal configurado: {configuration.Terminal.Command}.");
            StartTuiIfConfigured();
            benchmarkRecorder.Start();
            notificationBridgeClient?.Start();
            if (configuration.Notifications.Enabled) NotificationBridgeLauncher.Start(logger);
            if (benchmarkRecorder.IsEnabled)
            {
                var message = $"Benchmark detallado activo. Los registros se guardan en '{benchmarkRecorder.DirectoryPath}' y pueden incluir títulos y comandos.";
                logger.Info(message);
                Console.WriteLine(message);
            }
            logger.Info(stopExplorerAfterHotkeys
                ? "Modo de prueba sin Explorer solicitado."
                : "Modo de desarrollo con Explorer disponible.");
            Console.WriteLine("TenchyShell iniciado. Presiona Ctrl+C para salir durante el desarrollo.");
            Console.WriteLine($"Recuperación: {configuration.Hotkeys.Recovery} inicia explorer.exe.");
            return messageLoop.Run(() =>
            {
                try
                {
                    localApiServer?.Start();
                    localApiWindowMonitor?.Start();
                    localApiApplicationWatcher?.Start();
                }
                catch (Exception exception)
                {
                    logger.Error("No se pudo iniciar completamente la API local; TenchyShell continuará sin esa integración.", exception);
                }
                if (exitAfterSeconds.HasValue)
                {
                    automaticStopTimer = new Timer(
                        _ =>
                        {
                            diagnosticCancellation.Cancel();
                            messageLoop.Stop();
                        },
                        null,
                        TimeSpan.FromSeconds(exitAfterSeconds.Value),
                        Timeout.InfiniteTimeSpan);
                    logger.Info($"Cierre automático configurado en {exitAfterSeconds.Value} segundos.");
                }

                if (stopExplorerAfterHotkeys)
                {
                    StopExplorerAfterHotkeys();
                }

                RunPackagedActivationDiagnostics(packagedDiagnostic, diagnosticCancellation.Token);
                if (packagedDiagnostic is not null)
                {
                    // El límite de --exit-after-seconds sigue siendo la protección ante
                    // una llamada COM que no regrese. Cuando la matriz sí termina, no
                    // mantenemos la sesión artificialmente: permite detener una traza
                    // ETW inmediatamente después de la última activación.
                    messageLoop.Stop();
                    return;
                }

                layoutInteractionHost?.Start();
                RestoreWallpaperOnStartup();
                if (showBenchmarkTestNotification)
                {
                    ShowBenchmarkTestNotification();
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
            if (launcherWindow is not null)
            {
                launcherWindow.ApplicationSelected -= OnApplicationSelected;
                launcherWindow.CommandRequested -= OnCommandRequested;
            }
            if (applicationLauncher is not null)
            {
                applicationLauncher.PackagedApplicationActivationFailed -= OnPackagedApplicationActivationFailed;
            }
            if (statusPanelWindow is not null)
            {
                statusPanelWindow.TrayRequested -= OpenSystemTrayFromPointer;
                statusPanelWindow.DockItemRequested -= OnDockItemRequested;
                statusPanelWindow.Closed -= CancelSessionConfirmation;
                systemTrayService.VisibilityChanged -= OnSystemTrayVisibilityChanged;
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
        localApiServer?.Dispose();
        localApiWindowMonitor?.Dispose();
        localApiApplicationWatcher?.Dispose();
        localApiStateProvider?.Dispose();
        if (notificationBridgeClient is not null)
        {
            notificationBridgeClient.NotificationReceived -= OnNotificationReceived;
            notificationBridgeClient.NotificationRemoved -= notificationCenter.Remove;
            notificationCenter.DismissRequested -= notificationBridgeClient.Dismiss;
            notificationBridgeClient.Dispose();
        }
        notificationWindow?.Dispose();
        benchmarkRecorder.Dispose();
        minimizedWindowVisibilityService?.Dispose();
        systemTrayService.Dispose();
        wallpaperService.Dispose();
        windowSwitcherAltTabHook?.Dispose();
        windowSwitcherWindow?.Dispose();
        statusPanelWindow?.Dispose();
        messageLoop.Dispose();
        launcherWindow?.Dispose();
        logger.Info($"Resumen de estabilidad de sesión: {stabilityMetrics.Snapshot()}.");
        logger.Info("TenchyShell finalizado; se liberaron hotkeys y recursos Win32.");
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void ShowBenchmarkTestNotification()
    {
        var notification = new ShellNotification(
            $"benchmark-{Guid.NewGuid():N}",
            "TenchyShell.Benchmark",
            "TenchyShell",
            "Notificación de prueba",
            "La superficie de notificaciones está activa en modo benchmark. Este texto usa varias líneas para comprobar que la tarjeta calcula su altura según el contenido completo y no oculta el final del mensaje.",
            DateTimeOffset.Now);
        notificationCenter.Add(notification);
        benchmarkRecorder.Record("notification_test_shown");
        logger.Info("Notificación local de prueba mostrada en modo benchmark.");
    }

    private void OnRecoveryRequested()
    {
        var state = explorerShellController.GetCurrentSessionState();
        if (state.State != ExplorerShellState.Stopped)
        {
            var message = $"No se iniciará explorer.exe para evitar un duplicado. {state.Message}";
            logger.Error(message);
            benchmarkRecorder.Record("explorer_recovery_blocked", new { state = state.State.ToString(), state.Message, state.ProcessId });
            Console.Error.WriteLine(message);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });
            desktopAreaPolicy.UseMonitorArea = false;
            logger.Info("Se inició explorer.exe mediante el hotkey de recuperación.");
            benchmarkRecorder.Record("explorer_recovery_started");
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo iniciar explorer.exe mediante el hotkey de recuperación.", exception);
            benchmarkRecorder.Record("explorer_recovery_failed", new { exception.Message });
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
            if (isDisposed)
            {
                if (!string.IsNullOrWhiteSpace(state.Error)) logger.Error(state.Error);
                return;
            }

            if (state.IsNoBackground)
            {
                var noBackgroundResult = wallpaperService.ApplyNoBackground();
                if (!noBackgroundResult.Succeeded) logger.Error(noBackgroundResult.Error ?? "No se pudo restaurar el fondo negro.");
                return;
            }

            if (!state.HasSavedWallpaper)
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
        logger.Info("El modo sin Explorer solicitará la salida cooperativa del shell tras registrar el hotkey de recuperación.");
        var result = explorerShellController.TryExitCurrentSession();
        if (!result.CanContinueWithoutExplorer)
        {
            throw new InvalidOperationException(result.Message);
        }

        logger.Info(result.State == ExplorerShellState.ResidualProcess
            ? $"{result.Message} TenchyShell continuará; el proceso residual no se termina por la fuerza."
            : result.Message);
        stabilityMetrics.RecordExplorerTransition();
        desktopAreaPolicy.UseMonitorArea = true;
    }

    private void RunPackagedActivationDiagnostics(
        PackagedActivationDiagnosticOptions? options,
        CancellationToken cancellationToken)
    {
        if (options is null) return;

        var runner = new WindowsPackagedApplicationActivator();
        foreach (var appUserModelId in options.AppUserModelIds)
        {
            foreach (var context in options.Contexts)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var explorerState = explorerShellController.GetCurrentSessionState().State.ToString();
                var result = runner.Diagnose(appUserModelId, context, explorerState);
                ReportPackagedActivationDiagnostic(result);
            }
        }
    }

    private void ReportPackagedActivationDiagnostic(PackagedActivationDiagnosticResult result)
    {
        var consoleMessage = $"[DIAGNÓSTICO MSIX] AUMID='{result.AppUserModelId}' contexto={ToCommandLineContext(result.Context)} " +
                             $"explorer={result.ExplorerShellState} resultado={(result.Succeeded ? "éxito" : "fallo")} " +
                             $"pid={result.ProcessId?.ToString() ?? "desconocido"} etapa={result.Stage} HRESULT={result.HResultHex} " +
                             $"inicio={result.StartedAt:O} fin={result.FinishedAt:O} mensaje={result.Message}";
        Console.WriteLine(consoleMessage);

        var logMessage = $"Diagnóstico de activación empaquetada: contexto={ToCommandLineContext(result.Context)}, " +
                         $"explorer={result.ExplorerShellState}, resultado={(result.Succeeded ? "éxito" : "fallo")}, HRESULT={result.HResultHex}.";
        if (result.Succeeded) logger.Info(logMessage);
        else logger.Error(logMessage);
    }

    private static string ToCommandLineContext(PackagedApplicationActivationContext context) => context switch
    {
        PackagedApplicationActivationContext.InProcess => "in-process",
        PackagedApplicationActivationContext.LocalServer => "local-server",
        _ => context.ToString()
    };

    private void StartTuiIfConfigured()
    {
        if (!startTui)
        {
            logger.Info("Inicio automático de la TUI deshabilitado por --without-tui.");
            return;
        }
        if (!configuration.Tui.Enabled)
        {
            logger.Info("Inicio automático de la TUI deshabilitado por configuración.");
            return;
        }

        try
        {
            var target = TuiProcessTarget.FromCurrentProcess();
            var path = string.IsNullOrWhiteSpace(effectiveConfigurationPath)
                ? ConfigurationPathResolver.GetDefaultPath()
                : effectiveConfigurationPath;
            var request = new ProcessLaunchRequest(
                configuration.Terminal.Command,
                TuiTerminalCommandBuilder.BuildArguments(configuration.Terminal.Command, target, path));
            var result = new ProcessLauncher(logger).Launch(request);
            if (result.Succeeded)
            {
                logger.Info($"TUI iniciada dentro de '{configuration.Terminal.Command}' (PID: {result.ProcessId?.ToString() ?? "desconocido"}).");
            }
            else
            {
                var message = $"No se pudo iniciar la TUI dentro del terminal: {result.Error}";
                logger.Error(message);
                Console.Error.WriteLine(message);
            }
        }
        catch (Exception exception)
        {
            logger.Error("No se pudo preparar el inicio automático de la TUI.", exception);
            Console.Error.WriteLine($"No se pudo iniciar la TUI: {exception.Message}");
        }
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

        if (optionalComponents.DockEnabled)
        {
            ConfigureHotkey(StatusPanelHotkeyId, "panel informativo", configuration.StatusPanel.Hotkey);
        }

        if (configuration.WindowSwitcher.Enabled)
        {
            if (IsAltTabHotkey(configuration.WindowSwitcher.Hotkey))
            {
                logger.Info("El selector de ventanas capturará Alt+Tab y mostrará solo el workspace actual.");
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

        if (optionalComponents.ApplicationSearchEnabled)
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
        benchmarkRecorder.Record("hotkey", new { id, action = GetHotkeyDescription(id).ActionName, configuredValue = GetHotkeyDescription(id).ConfiguredValue });

        switch (id)
        {
            case RecoveryHotkeyId:
                OnRecoveryRequested();
                break;
            case LauncherHotkeyId when optionalComponents.ApplicationSearchEnabled:
                launcherWindow?.Toggle();
                logger.Info(launcherWindow?.IsVisible == true ? "Launcher mostrado." : "Launcher ocultado.");
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
                FocusActiveWindowWithBenchmark();
                break;
            case StatusPanelHotkeyId when optionalComponents.DockEnabled:
                statusPanelWindow?.ToggleByHotkey();
                logger.Info(statusPanelWindow?.IsVisible == true
                    ? "Panel informativo mostrado mediante hotkey."
                    : "Panel informativo ocultado mediante hotkey.");
                break;
            case WindowSwitcherHotkeyId when configuration.WindowSwitcher.Enabled:
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

    private void FocusActiveWindowWithBenchmark()
    {
        benchmarkRecorder.Record("focus_operation_started");
        var stopwatch = Stopwatch.StartNew();
        var result = windowService.FocusActiveWindow();
        stopwatch.Stop();
        benchmarkRecorder.Record("focus_operation_completed", new
        {
            succeeded = result.Succeeded,
            error = result.Error,
            durationMs = stopwatch.Elapsed.TotalMilliseconds
        });
        ReportWindowOperation(result);
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

    private static bool IsAltTabHotkey(string hotkey) =>
        string.Equals(hotkey.Trim(), "Alt+Tab", StringComparison.OrdinalIgnoreCase);

    private void SwitchWorkspace(int workspace)
    {
        var result = workspaceManager.SwitchTo(workspace);
        if (result.Succeeded)
        {
            statusPanelWindow?.AnnounceWorkspace(workspace);
            minimizedWindowVisibilityService?.Synchronize();
            systemTrayService.RefreshMinimizedWindows();
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
            minimizedWindowVisibilityService?.Synchronize();
            systemTrayService.RefreshMinimizedWindows();
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

        var targetRect = LayoutZoneCalculator.ToWindowRect(zone, desktopAreaPolicy.GetArea(monitor));
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

    private void OpenSystemTrayFromPointer()
    {
        if (systemTrayService.TryOpenFromPointer(out var error))
        {
            logger.Info("Se solicitó acceso a la bandeja del sistema mediante el panel.");
            return;
        }

        logger.Error($"No se pudo abrir la bandeja del sistema: {error}");
        Console.Error.WriteLine($"No se pudo abrir la bandeja del sistema: {error}");
    }

    private void OnDockItemRequested(string action)
    {
        logger.Info($"Acción del dock solicitada: {action}.");
        if (!action.Equals("network", StringComparison.OrdinalIgnoreCase))
            systemTrayService.Close();
        switch (action.Trim().ToLowerInvariant())
        {
            case "terminal": ReportLaunchFailure("terminal", actions.LaunchTerminal()); break;
            case "files": ReportLaunchFailure("Yazi", actions.LaunchFiles()); break;
            case "browser": ReportLaunchFailure("navegador", actions.LaunchBrowser()); break;
            case "launcher": ReportLaunchFailure("launcher", actions.LaunchLauncher()); break;
            case "input-language": OpenInputLanguageSelector(); break;
            case "network":
                if (!systemTrayService.TryOpenNetworkMenu(out var networkError) && networkError is not null)
                    logger.Error(networkError);
                break;
            case "wallpaper":
                if (!systemTrayService.TryOpenWallpaperMenu(out var wallpaperError) && wallpaperError is not null)
                    logger.Error(wallpaperError);
                break;
            case "windows":
                if (!systemTrayService.TryOpenMinimizedWindowsMenu(out var windowsError) && windowsError is not null)
                    logger.Error(windowsError);
                break;
            case "logout": RequestSessionAction(SessionAction.Logout); break;
            case "shutdown": RequestSessionAction(SessionAction.Shutdown); break;
            case "restart": RequestSessionAction(SessionAction.Restart); break;
            case "recovery": OnRecoveryRequested(); break;
            default: logger.Error($"Acción del dock no reconocida: {action}."); break;
        }
    }

    private void RequestSessionAction(SessionAction action)
    {
        if (sessionConfirmation.Status == SessionConfirmationStatus.AwaitingConfirmation &&
            sessionConfirmation.PendingAction == action)
        {
            statusPanelWindow?.SetSessionConfirmation(SessionConfirmationStatus.Executing, action);
            logger.Info($"Ejecutando acción de sesión confirmada: {action}.");
            _ = sessionConfirmation.ConfirmAsync(sessionActions).ContinueWith(task =>
            {
                if (!task.Result) logger.Error($"La acción de sesión no pudo ejecutarse: {action}.");
                statusPanelWindow?.SetSessionConfirmation(sessionConfirmation.Status, sessionConfirmation.PendingAction);
            }, TaskScheduler.Default);
            return;
        }

        if (sessionConfirmation.Request(action))
        {
            statusPanelWindow?.SetSessionConfirmation(sessionConfirmation.Status, sessionConfirmation.PendingAction);
            logger.Info($"Se solicitó confirmación para la acción de sesión: {action}. Repite la acción para confirmar.");
        }
        else
        {
            statusPanelWindow?.SetSessionConfirmation(sessionConfirmation.Status, sessionConfirmation.PendingAction);
            logger.Info($"Se canceló la confirmación pendiente al solicitar otra acción de sesión: {action}.");
        }
    }

    private void CancelSessionConfirmation()
    {
        if (sessionConfirmation.Cancel())
        {
            statusPanelWindow?.SetSessionConfirmation(sessionConfirmation.Status, sessionConfirmation.PendingAction);
            logger.Info("Se canceló la confirmación de sesión.");
        }
    }

    private void OnSystemTrayVisibilityChanged(bool isVisible)
    {
        statusPanelWindow?.SetMenuActive("system-tray", isVisible);
    }

    private void OnNotificationReceived(NotificationBridgeReceivedEventArgs received)
    {
        notificationCenter.Add(received.Notification, showPopup: !received.Historical);
        benchmarkRecorder.Record("notification_received", new
        {
            received.Notification.AppId,
            hasIcon = received.Notification.IconPng is { Length: > 0 },
            received.Historical
        });
    }

    private void OnPackagedApplicationActivationFailed(object? sender, PackagedApplicationActivationFailedEventArgs args)
    {
        var result = args.Result;
        var detail = $"Falló la activación MSIX/UWP de '{args.Application.DisplayName}': " +
                     $"AUMID='{args.Application.AppUserModelId}', etapa={result.Stage}, " +
                     $"HRESULT={result.HResultHex}, timeout={(result.TimedOut ? "sí" : "no")}, detalle={result.Message}";
        logger.Error(detail);
        Console.Error.WriteLine(detail);

        var body = result.TimedOut
            ? "La solicitud no respondió en cinco segundos. Puede depender de componentes de sesión y Windows podría completarla después."
            : $"Windows devolvió {result.HResultHex}.";
        notificationCenter.Add(new ShellNotification(
            $"packaged-activation-{Guid.NewGuid():N}",
            "TenchyShell.PackagedActivation",
            "TenchyShell",
            $"No se pudo abrir {args.Application.DisplayName}",
            body,
            DateTimeOffset.UtcNow));
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
        if (applicationLauncher is null)
        {
            return;
        }

        benchmarkRecorder.Record("application_requested", new { application.DisplayName, application.Target, application.Arguments });
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
        benchmarkRecorder.Record("command_requested", new { command });
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
        benchmarkRecorder.Record("fatal_error", new { exception.Message, exception.StackTrace });
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

    private void LogOptionalComponentState()
    {
        logger.Info($"Estado efectivo del dock: {(optionalComponents.DockEnabled ? "habilitado" : "deshabilitado")}.");
        if (!optionalComponents.DockEnabled)
        {
            logger.Info($"Dock opcional deshabilitado; no se creará su superficie ni se registrará su hotkey. Motivo: {optionalComponents.DockDisabledReason}");
        }

        logger.Info($"Estado efectivo del buscador de aplicaciones: {(optionalComponents.ApplicationSearchEnabled ? "habilitado" : "deshabilitado")}.");
        if (!optionalComponents.ApplicationSearchEnabled)
        {
            logger.Info($"Buscador de aplicaciones opcional deshabilitado; no se crearán catálogo ni launcher. Motivo: {optionalComponents.ApplicationSearchDisabledReason}");
        }
    }
}
