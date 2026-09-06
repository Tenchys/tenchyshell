using System.Reflection;

namespace TenchyShell.App;

public sealed record TuiProcessTarget(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public static TuiProcessTarget FromCurrentProcess()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("No se pudo determinar la ruta del proceso TenchyShell.");
        }

        var processName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "dotnet-watch", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            var assemblyPath = string.IsNullOrWhiteSpace(assemblyName)
                ? null
                : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("No se pudo determinar el ensamblado de TenchyShell.");
            }

            return new TuiProcessTarget(processPath, new[] { assemblyPath });
        }

        return new TuiProcessTarget(processPath, Array.Empty<string>());
    }
}

public static class TuiTerminalCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(
        string terminalCommand,
        TuiProcessTarget target,
        string configurationPath)
    {
        var targetArguments = target.PrefixArguments
            .Concat(new[] { "--tui", configurationPath })
            .ToArray();
        var terminalName = Path.GetFileNameWithoutExtension(terminalCommand);
        var result = new List<string>();
        if (string.Equals(terminalName, "wt", StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(new[] { "-w", "0", "new-tab", "--" });
        }
        else
        {
            result.AddRange(new[] { "start", "--always-new-process", "--" });
        }

        result.Add(target.FileName);
        result.AddRange(targetArguments);
        return result;
    }
}
