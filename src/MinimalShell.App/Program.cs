using MinimalShell.Core.Configuration;
using MinimalShell.Core.Logging;
using MinimalShell.Core.Commands;
using MinimalShell.Core.Session;
using MinimalShell.Core.Runtime;

namespace MinimalShell.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        var logger = new FileLogger();
        var launchIndex = Array.FindIndex(args, argument => argument.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        var sessionIndex = Array.FindIndex(args, argument => argument.Equals("--session", StringComparison.OrdinalIgnoreCase));
        var withoutExplorer = args.Any(argument => argument.Equals("--without-explorer", StringComparison.OrdinalIgnoreCase));
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

        if (!SingleInstanceGuard.TryAcquire("Local\\MinimalShell.SingleInstance", out var instanceGuard))
        {
            const string message = "MinimalShell ya está ejecutándose. Cierra la instancia actual antes de iniciar otra.";
            logger.Error(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        using (instanceGuard)
        {
            using var shell = new ShellHost(result.Configuration, logger, withoutExplorer);
            return shell.Run();
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
                args[index].Equals("--session", StringComparison.OrdinalIgnoreCase))
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

    private static MinimalShell.Core.Processes.ProcessLaunchResult ExecuteLaunchAction(
        ShellActions actions,
        string actionName) => actionName.ToLowerInvariant() switch
        {
            "terminal" => actions.LaunchTerminal(),
            "files" or "yazi" => actions.LaunchFiles(),
            "browser" => actions.LaunchBrowser(),
            _ => MinimalShell.Core.Processes.ProcessLaunchResult.Failure(
                "Acción no válida. Usa: terminal, files o browser.")
        };

    private static MinimalShell.Core.Processes.ProcessLaunchResult ExecuteSessionAction(
        SessionActionService actions,
        string actionName,
        bool confirmed) => actionName.ToLowerInvariant() switch
        {
            "logout" => actions.Execute(SessionAction.Logout, confirmed),
            "shutdown" => actions.Execute(SessionAction.Shutdown, confirmed),
            "restart" or "reboot" => actions.Execute(SessionAction.Restart, confirmed),
            _ => MinimalShell.Core.Processes.ProcessLaunchResult.Failure(
                "Acción de sesión no válida. Usa: logout, shutdown o restart.")
        };
}
