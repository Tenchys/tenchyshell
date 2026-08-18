using System.Diagnostics;
using MinimalShell.Core.Applications;
using MinimalShell.Core.Commands;
using MinimalShell.Core.Configuration;
using MinimalShell.Core.Logging;
using MinimalShell.Core.Processes;
using MinimalShell.Core.Windows;
using MinimalShell.Win32;
using MinimalShell.Workspaces;

namespace MinimalShell.App;

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
    private bool isDisposed;

    public ShellHost(ShellConfiguration configuration, ILogger logger, bool stopExplorerAfterHotkeys = false)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.stopExplorerAfterHotkeys = stopExplorerAfterHotkeys;
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
    }

    public ShellActions Actions => actions;

    public int Run()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Console.CancelKeyPress += OnCancelKeyPress;
        messageLoop.FatalError += OnFatalError;
        messageLoop.HotkeyPressed += OnHotkeyPressed;
        messageLoop.HotkeyRegistered += OnHotkeyRegistered;
        messageLoop.HotkeyRegistrationFailed += OnHotkeyRegistrationFailed;
        launcherWindow.ApplicationSelected += OnApplicationSelected;
        launcherWindow.CommandRequested += OnCommandRequested;

        try
        {
            ConfigureHotkeys();
            workspaceManager.Refresh();
            statusPanelWindow?.SetWorkspace(workspaceManager.CurrentWorkspace);
            statusPanelWindow?.Start();
            applicationCatalog.Refresh();
            logger.Info($"Catálogo de aplicaciones cargado: {applicationCatalog.GetAll().Count} entradas.");
            logger.Info($"MinimalShell iniciado. Terminal configurado: {configuration.Terminal.Command}.");
            logger.Info(stopExplorerAfterHotkeys
                ? "Modo de prueba sin Explorer solicitado."
                : "Modo de desarrollo con Explorer disponible.");
            Console.WriteLine("MinimalShell iniciado. Presiona Ctrl+C para salir durante el desarrollo.");
            Console.WriteLine($"Recuperación: {configuration.Hotkeys.Recovery} inicia explorer.exe.");
            return messageLoop.Run(stopExplorerAfterHotkeys ? StopExplorerAfterHotkeys : null);
        }
        catch (Exception exception)
        {
            OnFatalError(exception);
            return 1;
        }
        finally
        {
            messageLoop.FatalError -= OnFatalError;
            messageLoop.HotkeyPressed -= OnHotkeyPressed;
            messageLoop.HotkeyRegistered -= OnHotkeyRegistered;
            messageLoop.HotkeyRegistrationFailed -= OnHotkeyRegistrationFailed;
            launcherWindow.ApplicationSelected -= OnApplicationSelected;
            launcherWindow.CommandRequested -= OnCommandRequested;
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

        statusPanelWindow?.Dispose();
        messageLoop.Dispose();
        launcherWindow.Dispose();
        logger.Info("MinimalShell finalizado; se liberaron hotkeys y recursos Win32.");
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

    private void StopExplorerAfterHotkeys()
    {
        var explorers = Process.GetProcessesByName("explorer");

        if (explorers.Length == 0)
        {
            logger.Info("El modo sin Explorer está activo; no había procesos explorer.exe para cerrar.");
            return;
        }

        logger.Info($"El modo sin Explorer cerrará {explorers.Length} proceso(s) explorer.exe tras registrar el hotkey de recuperación.");

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

        if (configuration.Launcher.Enabled)
        {
            ConfigureHotkey(LauncherHotkeyId, "launcher", configuration.Hotkeys.Launcher);
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
        logger.Error("Error no controlado en MinimalShell.", exception);
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
