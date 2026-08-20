using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Processes;

namespace TenchyShell.Core.Commands;

public sealed class ShellActions
{
    private readonly ShellConfiguration configuration;
    private readonly IProcessLauncher processLauncher;
    private readonly ILogger logger;

    public ShellActions(
        ShellConfiguration configuration,
        IProcessLauncher processLauncher,
        ILogger logger)
    {
        this.configuration = configuration;
        this.processLauncher = processLauncher;
        this.logger = logger;
    }

    public ProcessLaunchResult LaunchTerminal() => Launch(
        "terminal",
        new ProcessLaunchRequest(configuration.Terminal.Command));

    public ProcessLaunchResult LaunchFiles() => Launch(
        "Yazi",
        new ProcessLaunchRequest(
            configuration.Terminal.Command,
            BuildFileManagerArguments()));

    public ProcessLaunchResult LaunchLauncher() => Launch(
        "Command Palette",
        new ProcessLaunchRequest(configuration.Launcher.Command));

    public ProcessLaunchResult LaunchBrowser() => Launch(
        "navegador",
        new ProcessLaunchRequest(configuration.Applications.Browser));

    public ProcessLaunchResult LaunchCommand(string command)
    {
        var normalizedCommand = command.Trim();

        if (normalizedCommand.Length == 0)
        {
            var result = ProcessLaunchResult.Failure("El comando no puede estar vacío.");
            logger.Error(result.Error!);
            return result;
        }

        var arguments = configuration.Terminal.CommandArguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(argument => argument.Equals("{shell}", StringComparison.OrdinalIgnoreCase)
                ? configuration.Terminal.CommandShell
                : argument)
            .Append(normalizedCommand);

        return Launch(
            "comando de terminal",
            new ProcessLaunchRequest(configuration.Terminal.Command, arguments));
    }

    public ProcessLaunchResult LaunchApplication(string fileName, params string[] arguments) => Launch(
        $"aplicación '{fileName}'",
        new ProcessLaunchRequest(fileName, arguments));

    private ProcessLaunchResult Launch(string actionName, ProcessLaunchRequest request)
    {
        var result = processLauncher.Launch(request);

        if (result.Succeeded)
        {
            logger.Info($"Se lanzó {actionName} (PID: {result.ProcessId?.ToString() ?? "desconocido"}).");
        }
        else
        {
            logger.Error($"No se pudo lanzar {actionName}: {result.Error}");
        }

        return result;
    }

    private IEnumerable<string> BuildFileManagerArguments()
    {
        foreach (var argument in configuration.Terminal.FileManagerArguments.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return argument;
        }

        yield return configuration.FileManager.Command;
    }
}
