using MinimalShell.Core.Configuration;
using MinimalShell.Core.Layout;
using Microsoft.Win32;

namespace MinimalShell.Core.Diagnostics;

public interface ICommandAvailabilityChecker
{
    bool IsAvailable(string command);
}

public sealed class EnvironmentCommandAvailabilityChecker : ICommandAvailabilityChecker
{
    public bool IsAvailable(string command)
    {
        var normalized = command.Trim().Trim('"');
        if (normalized.Length == 0)
        {
            return false;
        }

        if (Path.IsPathFullyQualified(normalized))
        {
            return File.Exists(normalized);
        }

        if (File.Exists(normalized))
        {
            return true;
        }

        if (IsRegisteredApplication(normalized))
        {
            return true;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = GetExecutableExtensions(normalized);

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), normalized + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRegisteredApplication(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var subKeyName = $"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\{command}";
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };

        try
        {
            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appKey = baseKey.OpenSubKey(subKeyName);
                    var executable = appKey?.GetValue(string.Empty) as string;

                    var expandedExecutable = Environment.ExpandEnvironmentVariables(executable ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(expandedExecutable) && File.Exists(expandedExecutable.Trim().Trim('"')))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Un registro no accesible no debe impedir el arranque del shell.
        }

        return false;
    }

    private static IReadOnlyList<string> GetExecutableExtensions(string command)
    {
        if (Path.HasExtension(command))
        {
            return new[] { string.Empty };
        }

        var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExtensions))
        {
            return new[] { string.Empty, ".exe", ".cmd", ".bat", ".com" };
        }

        return new[] { string.Empty }
            .Concat(pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
    }
}

public sealed record StartupDiagnostic(string Component, string Command, bool IsAvailable);

public sealed class StartupDiagnosticsResult
{
    public StartupDiagnosticsResult(IReadOnlyList<StartupDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<StartupDiagnostic> Diagnostics { get; }

    public bool HasMissingDependencies => Diagnostics.Any(diagnostic => !diagnostic.IsAvailable);
}

public static class StartupDiagnostics
{
    public static StartupDiagnosticsResult Run(
        ShellConfiguration configuration,
        ICommandAvailabilityChecker availabilityChecker)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(availabilityChecker);

        var diagnostics = new List<StartupDiagnostic>
        {
            Check("terminal", configuration.Terminal.Command, availabilityChecker),
            Check("shell de comandos", configuration.Terminal.CommandShell, availabilityChecker),
            Check("file manager", configuration.FileManager.Command, availabilityChecker),
            Check("navegador", configuration.Applications.Browser, availabilityChecker),
            CheckLayout(configuration)
        };

        return new StartupDiagnosticsResult(diagnostics);
    }

    private static StartupDiagnostic Check(
        string component,
        string command,
        ICommandAvailabilityChecker availabilityChecker) =>
        new(component, command, availabilityChecker.IsAvailable(command));

    private static StartupDiagnostic CheckLayout(ShellConfiguration configuration)
    {
        if (!configuration.Layout.Enabled)
        {
            return new StartupDiagnostic("layout", "deshabilitado", true);
        }

        var validation = LayoutZoneValidator.Validate(
            configuration.Layout.Zones,
            configuration.Layout.MaxZones);
        var validPreset = string.Equals(
            configuration.Layout.DefaultPreset,
            "1x2",
            StringComparison.OrdinalIgnoreCase);
        var validLabelSize = configuration.Layout.ZoneNumberSizePercent is > 0 and <= 25;
        var isAvailable = validation.IsValid && validPreset && validLabelSize;
        var description = $"{configuration.Layout.Zones.Count} zonas; preset {configuration.Layout.DefaultPreset}";

        return new StartupDiagnostic("layout", description, isAvailable);
    }
}
