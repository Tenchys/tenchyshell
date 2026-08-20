using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Session;
using TenchyShell.Core.Runtime;

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

        var migration = LegacyDataMigrator.MigrateDefault();
        var logger = new FileLogger();
        ReportLegacyMigration(logger, migration);
        var launchIndex = Array.FindIndex(args, argument => argument.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        var sessionIndex = Array.FindIndex(args, argument => argument.Equals("--session", StringComparison.OrdinalIgnoreCase));
        var withoutExplorer = args.Any(argument => argument.Equals("--without-explorer", StringComparison.OrdinalIgnoreCase));
        var checkOnly = HasArgument(args, "--check");
        if (!TryGetOptionalPositiveInteger(args, "--exit-after-seconds", 3600, out var exitAfterSeconds, out var optionError))
        {
            Console.Error.WriteLine(optionError);
            return 1;
        }
        var configurationPath = GetConfigurationPath(args);
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

        if (withoutExplorer && !ConfirmExplorerShutdown())
        {
            Console.Error.WriteLine("Inicio cancelado: explorer.exe no fue cerrado.");
            return 1;
        }

        if (!SingleInstanceGuard.TryAcquire("Local\\MinimalShell.SingleInstance", out var legacyInstanceGuard))
        {
            const string message = "TenchyShell ya está ejecutándose. Cierra la instancia actual antes de iniciar otra.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        if (!SingleInstanceGuard.TryAcquire("Local\\TenchyShell.SingleInstance", out var instanceGuard))
        {
            legacyInstanceGuard!.Dispose();
            const string message = "TenchyShell ya está ejecutándose. Cierra la instancia actual antes de iniciar otra.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        using (legacyInstanceGuard!)
        using (instanceGuard!)
        {
            using var shell = new ShellHost(result.Configuration, logger, withoutExplorer);
            return shell.Run(exitAfterSeconds);
        }
    }

    private static bool ConfirmExplorerShutdown()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("--without-explorer requiere una consola interactiva para confirmar la operación.");
            return false;
        }

        Console.WriteLine("ADVERTENCIA: se cerrará explorer.exe después de registrar los hotkeys.");
        Console.WriteLine("Usa una VM o un usuario secundario. Escribe DETENER para continuar:");
        return string.Equals(Console.ReadLine()?.Trim(), "DETENER", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetConfigurationPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--launch", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--session", StringComparison.OrdinalIgnoreCase) ||
                args[index].Equals("--exit-after-seconds", StringComparison.OrdinalIgnoreCase))
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
        Console.WriteLine("  TenchyShell.exe --exit-after-seconds N [config.toml]");
        Console.WriteLine("  TenchyShell.exe --session logout|shutdown|restart --confirm [config.toml]");
        Console.WriteLine();
        Console.WriteLine("--check valida dependencias y configuración sin registrar hotkeys ni cerrar Explorer.");
        Console.WriteLine("--without-explorer requiere confirmación y solo debe usarse en una VM o usuario secundario.");
        Console.WriteLine("--exit-after-seconds cierra limpiamente una sesión de benchmark o integración.");
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
