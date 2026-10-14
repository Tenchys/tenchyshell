using System.Diagnostics;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Applications;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Session;
using TenchyShell.Core.Runtime;
using TenchyShell.Core.Startup;
using TenchyShell.Win32;
using TenchyShell.Tui;

namespace TenchyShell.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (HasArgument(args, "--help") || HasArgument(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        if (!TryGetOptionalArgumentValue(args, "--session-id", out var requestedSessionId, out var sessionIdError))
        {
            Console.Error.WriteLine(sessionIdError);
            return 1;
        }

        var migration = LegacyDataMigrator.MigrateDefault();
        var runId = requestedSessionId ?? Guid.NewGuid().ToString("N");
        var logger = new FileLogger(sessionId: runId);
        ReportLegacyMigration(logger, migration);
        if (!PackagedActivationDiagnosticOptions.TryParse(args, out var packagedDiagnostic, out var packagedDiagnosticError))
        {
            Console.Error.WriteLine(packagedDiagnosticError);
            return 1;
        }
        if (HasArgument(args, "--tui"))
        {
            if (packagedDiagnostic is not null)
            {
                Console.Error.WriteLine("--diagnose-packaged no se puede combinar con --tui.");
                return 1;
            }
            if (!TuiArgumentParser.TryGetConfigurationPath(args, out var tuiPath, out var tuiError))
            {
                Console.Error.WriteLine(tuiError);
                return 1;
            }

            return TuiApplication.Run(Path.GetFullPath(tuiPath!), logger);
        }

        var launchIndex = Array.FindIndex(args, argument => argument.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        var sessionIndex = Array.FindIndex(args, argument => argument.Equals("--session", StringComparison.OrdinalIgnoreCase));
        var withoutExplorer = args.Any(argument => argument.Equals("--without-explorer", StringComparison.OrdinalIgnoreCase));
        var automatedBenchmark = HasArgument(args, "--automated-benchmark");
        var testNotification = HasArgument(args, "--test-notification");
        var checkOnly = HasArgument(args, "--check");
        var withoutTui = HasArgument(args, "--without-tui");
        var keepSplash = HasArgument(args, "--keep-splash");
        if (!TryGetOptionalPositiveInteger(args, "--exit-after-seconds", 3600, out var exitAfterSeconds, out var optionError))
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }

        if (automatedBenchmark && (!withoutExplorer || !exitAfterSeconds.HasValue))
        {
            Console.Error.WriteLine("--automated-benchmark requiere --without-explorer y --exit-after-seconds.");
            return 1;
        }

        if (packagedDiagnostic is not null && !exitAfterSeconds.HasValue)
        {
            Console.Error.WriteLine("--diagnose-packaged requiere --exit-after-seconds.");
            return 1;
        }

        var configurationPath = ConfigurationPathResolver.Resolve(GetConfigurationPath(args));
        var effectiveConfigurationPath = configurationPath is null
            ? ConfigurationPathResolver.GetDefaultPath()
            : Path.GetFullPath(configurationPath);
        var result = new TomlConfigurationProvider(logger).Load(configurationPath);

        if (!result.IsValid)
        {
            Console.Error.WriteLine("No se pudo cargar la configuración:");

            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"- {error}");
            }

            return 1;
        }

        logger.ConsoleEnabled = result.Configuration.Logging.Console;
        logger.MetricsEnabled = result.Configuration.Benchmark.Enabled;

        if (testNotification && !result.Configuration.Benchmark.Enabled)
        {
            Console.Error.WriteLine("--test-notification requiere [benchmark] enabled = true.");
            return 1;
        }

        if (testNotification && !result.Configuration.Notifications.Enabled)
        {
            Console.Error.WriteLine("--test-notification requiere [notifications] enabled = true.");
            return 1;
        }

        var diagnostics = StartupDiagnostics.Run(
            result.Configuration,
            new EnvironmentCommandAvailabilityChecker());
        ReportStartupDiagnostics(logger, diagnostics);

        if (checkOnly)
        {
            return diagnostics.HasMissingDependencies ? 2 : 0;
        }

        if (launchIndex >= 0)
        {
            if (sessionIndex >= 0)
            {
                Console.Error.WriteLine("Usa solo una de las opciones: --launch o --session.");
                return 1;
            }

            var actionName = launchIndex + 1 < args.Length ? args[launchIndex + 1] : string.Empty;
            var actions = new ShellActions(result.Configuration, new ProcessLauncher(logger), logger);
            var launchResult = ExecuteLaunchAction(actions, actionName);

            if (!launchResult.Succeeded)
            {
                Console.Error.WriteLine(launchResult.Error);
                return 1;
            }

            Console.WriteLine($"Proceso iniciado (PID: {launchResult.ProcessId?.ToString() ?? "desconocido"}).");
            return 0;
        }

        if (sessionIndex >= 0)
        {
            var actionName = sessionIndex + 1 < args.Length ? args[sessionIndex + 1] : string.Empty;
            var sessionResult = ExecuteSessionAction(
                new SessionActionService(new ProcessLauncher(logger), logger),
                actionName,
                args.Any(argument => argument.Equals("--confirm", StringComparison.OrdinalIgnoreCase)));

            if (!sessionResult.Succeeded)
            {
                Console.Error.WriteLine(sessionResult.Error);
                return 1;
            }

            Console.WriteLine("Acción de sesión solicitada.");
            return 0;
        }

        if (automatedBenchmark)
            logger.Info($"Benchmark automatizado solicitado; la telemetría live se conservará y no habrá recuperación automática de Explorer. sessionId={runId}.");

        var showStartupSplash = !automatedBenchmark &&
                                packagedDiagnostic is null &&
                                !HasArgument(args, "--tui") &&
                                launchIndex < 0 &&
                                sessionIndex < 0;
        StartupProgressState? startupProgress = null;
        StartupSplashWindow? startupSplash = null;
        if (showStartupSplash)
        {
            startupProgress = new StartupProgressState();
            try
            {
                startupSplash = new StartupSplashWindow(startupProgress, keepVisibleWhenReady: keepSplash);
                startupProgress.ReportStarted(
                    StartupModule.MigrationAndConfiguration,
                    "Migrando datos y validando la configuración");
                startupProgress.ReportCompleted(StartupModule.MigrationAndConfiguration);
            }
            catch (Exception exception)
            {
                startupSplash?.Dispose();
                startupProgress?.Close();
                logger.Error("No se pudo crear el splash de inicio; se aborta el arranque.", exception);
                Console.Error.WriteLine($"No se pudo crear el splash de inicio: {exception.Message}");
                return 1;
            }
        }

        if (!SingleInstanceGuard.TryAcquire("Local\\MinimalShell.SingleInstance", out var legacyInstanceGuard))
        {
            const string message = "TenchyShell ya está ejecutándose. Cierra la instancia actual antes de iniciar otra.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            startupProgress?.Close();
            startupSplash?.Dispose();
            return 1;
        }

        if (!SingleInstanceGuard.TryAcquire("Local\\TenchyShell.SingleInstance", out var instanceGuard))
        {
            legacyInstanceGuard!.Dispose();
            const string message = "TenchyShell ya está ejecutándose. Cierra la instancia actual antes de iniciar otra.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            startupProgress?.Close();
            startupSplash?.Dispose();
            return 1;
        }

        try
        {
            using (legacyInstanceGuard!)
            using (instanceGuard!)
            {
                startupProgress?.ReportStarted(
                    StartupModule.CoreServices,
                    "Inicializando servicios principales y lanzamiento de procesos");
                using var shell = new ShellHost(
                    result.Configuration,
                    logger,
                    withoutExplorer,
                    startTui: !withoutTui && packagedDiagnostic is null,
                    effectiveConfigurationPath: effectiveConfigurationPath,
                    sessionId: runId,
                    suppressLiveBenchmark: automatedBenchmark,
                    startupProgress: startupProgress);
                startupProgress?.ReportCompleted(StartupModule.CoreServices);
                return shell.Run(exitAfterSeconds, testNotification, packagedDiagnostic);
            }
        }
        catch (Exception exception)
        {
            ReportStartupFailure(startupProgress, exception);
            startupSplash?.Close();
            logger.Error("Error no controlado al ejecutar TenchyShell.", exception);
            Console.Error.WriteLine($"Error no controlado: {exception.Message}");
            return 1;
        }
        finally
        {
            startupProgress?.Close();
            startupSplash?.Dispose();
        }
    }

    private static string? GetConfigurationPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--session", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--exit-after-seconds", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--session-id", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--diagnose-packaged", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--activation-context", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return args[index];
            }
        }

        return null;
    }

    private static bool TryGetOptionalArgumentValue(
        IReadOnlyList<string> args,
        string option,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        var index = -1;
        for (var current = 0; current < args.Count; current++)
        {
            if (!args[current].Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index >= 0)
            {
                error = $"La opción {option} no puede repetirse.";
                return false;
            }
            index = current;
        }

        if (index < 0) return true;
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) ||
            !System.Text.RegularExpressions.Regex.IsMatch(args[index + 1], "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
        {
            error = $"{option} requiere un identificador alfanumérico válido.";
            return false;
        }

        value = args[index + 1];
        return true;
    }

    private static bool TryGetOptionalPositiveInteger(
        IReadOnlyList<string> args,
        string option,
        int maximum,
        out int? value,
        out string? error)
    {
        value = null;
        error = null;
        var index = -1;
        for (var current = 0; current < args.Count; current++)
        {
            if (!args[current].Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index >= 0)
            {
                error = $"La opción {option} no puede repetirse.";
                return false;
            }
            index = current;
        }
        if (index < 0) return true;
        if (index + 1 >= args.Count || !int.TryParse(args[index + 1], out var parsed) || parsed is < 1 || parsed > maximum)
        {
            error = $"{option} requiere un entero entre 1 y {maximum}.";
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool HasArgument(IEnumerable<string> args, string argument) =>
        args.Any(value => value.Equals(argument, StringComparison.OrdinalIgnoreCase));

    private static void ReportLegacyMigration(ILogger logger, LegacyMigrationResult migration)
    {
        foreach (var item in migration.Items.Where(item => item.Status != MigrationItemStatus.Missing))
        {
            var message = $"Migración {item.Status}: {item.SourceRelativePath} -> {item.TargetRelativePath}";
            if (item.Status is MigrationItemStatus.Conflict or MigrationItemStatus.Invalid or MigrationItemStatus.Error)
            {
                logger.Error($"{message}. {item.Error}");
                Console.Error.WriteLine($"[MIGRACIÓN] {message}. {item.Error}");
            }
            else
            {
                logger.Info(message);
            }
        }
    }

    private static void ReportStartupFailure(StartupProgressState? progress, Exception exception)
    {
        if (progress is null) return;
        var snapshot = progress.Snapshot;
        if (snapshot.Status == StartupProgressStatus.InProgress && snapshot.CurrentModule is StartupModule module)
        {
            try
            {
                progress.ReportError(module, $"Falló {module}.", exception);
            }
            catch (InvalidOperationException)
            {
                // ShellHost puede haber informado ya el error fatal.
            }
        }
    }

    private static void ReportStartupDiagnostics(ILogger logger, StartupDiagnosticsResult diagnostics)
    {
        foreach (var diagnostic in diagnostics.Diagnostics)
        {
            if (diagnostic.IsAvailable)
            {
                var message = $"Diagnóstico: {diagnostic.Component} disponible ('{diagnostic.Command}').";
                logger.Info(message);
                Console.WriteLine($"[OK] {message}");
            }
            else
            {
                var message = $"Diagnóstico: no se encontró {diagnostic.Component} ('{diagnostic.Command}').";
                logger.Error(message);
                Console.Error.WriteLine($"[ADVERTENCIA] {message}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("TenchyShell — shell minimalista para Windows");
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  TenchyShell.exe [config.toml]");
        Console.WriteLine("  TenchyShell.exe --check [config.toml]");
        Console.WriteLine("  TenchyShell.exe --launch terminal|files|browser [config.toml]");
        Console.WriteLine("  TenchyShell.exe --without-explorer [config.toml]");
        Console.WriteLine("  TenchyShell.exe --without-tui [config.toml]");
        Console.WriteLine("  TenchyShell.exe --keep-splash [config.toml]");
        Console.WriteLine("  TenchyShell.exe --tui <config.toml>");
        Console.WriteLine("  TenchyShell.exe --exit-after-seconds N [config.toml]");
        Console.WriteLine("  TenchyShell.exe --session-id ID ... (uso interno de diagnóstico)");
        Console.WriteLine("  TenchyShell.exe --diagnose-packaged <AUMID> [--diagnose-packaged <AUMID> ...] [--activation-context in-process|local-server|both] --exit-after-seconds N [config.toml]");
        Console.WriteLine("  TenchyShell.exe --test-notification [config.toml]");
        Console.WriteLine("  TenchyShell.exe --automated-benchmark --without-explorer --exit-after-seconds N [config.toml]");
        Console.WriteLine("  TenchyShell.exe --session logout|shutdown|restart --confirm [config.toml]");
        Console.WriteLine();
        Console.WriteLine("--check valida dependencias y configuración sin registrar hotkeys ni cerrar Explorer.");
        Console.WriteLine("--without-explorer registra primero los hotkeys y solo debe usarse en una VM o usuario secundario.");
        Console.WriteLine("--exit-after-seconds cierra limpiamente una sesión de benchmark o integración.");
        Console.WriteLine("--test-notification muestra una tarjeta local; requiere benchmark y notificaciones habilitados.");
        Console.WriteLine("--automated-benchmark exige un cierre acotado y no restaura Explorer al terminar; la recuperación es manual.");
        Console.WriteLine("--without-tui inicia el shell sin abrir la TUI; --tui ejecuta la interfaz dentro del terminal.");
        Console.WriteLine("--keep-splash mantiene el splash visible al terminar el arranque; haz clic sobre él para cerrarlo.");
        Console.WriteLine("--diagnose-packaged compara la activación oficial por AUMID sin iniciar Explorer; por defecto prueba ambos contextos COM y no abre la TUI.");
    }

    private static TenchyShell.Core.Processes.ProcessLaunchResult ExecuteLaunchAction(
        ShellActions actions,
        string actionName) => actionName.ToLowerInvariant() switch
        {
            "terminal" => actions.LaunchTerminal(),
            "files" or "yazi" => actions.LaunchFiles(),
            "browser" => actions.LaunchBrowser(),
            _ => TenchyShell.Core.Processes.ProcessLaunchResult.Failure(
                "Acción no válida. Usa: terminal, files o browser.")
        };

    private static TenchyShell.Core.Processes.ProcessLaunchResult ExecuteSessionAction(
        SessionActionService actions,
        string actionName,
        bool confirmed) => actionName.ToLowerInvariant() switch
        {
            "logout" => actions.Execute(SessionAction.Logout, confirmed),
            "shutdown" => actions.Execute(SessionAction.Shutdown, confirmed),
            "restart" or "reboot" => actions.Execute(SessionAction.Restart, confirmed),
            _ => TenchyShell.Core.Processes.ProcessLaunchResult.Failure(
                "Acción de sesión no válida. Usa: logout, shutdown o restart.")
        };
}
