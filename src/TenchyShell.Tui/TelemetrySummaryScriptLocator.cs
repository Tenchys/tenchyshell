namespace TenchyShell.Tui;

/// <summary>Localiza el resumidor junto a la configuración o a la instalación actual.</summary>
public static class TelemetrySummaryScriptLocator
{
    public const string ScriptFileName = "summarize-live-telemetry.ps1";

    public static string? Find(string configurationPath, string? executablePath = null, string? currentDirectory = null)
    {
        var candidates = new List<string>();
        try
        {
            var configurationDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
            if (!string.IsNullOrWhiteSpace(configurationDirectory))
            {
                candidates.Add(Path.Combine(configurationDirectory, "scripts", ScriptFileName));
                var parent = Directory.GetParent(configurationDirectory)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(Path.Combine(parent, "scripts", ScriptFileName));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }

        var processPath = executablePath ?? Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var executableDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(executableDirectory)) candidates.Add(Path.Combine(executableDirectory, "scripts", ScriptFileName));
        }

        var directory = currentDirectory ?? Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory, "scripts", ScriptFileName));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => File.Exists(path));
    }
}
