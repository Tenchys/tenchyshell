namespace TenchyShell.Tui;

public static class TuiArgumentParser
{
    public static bool TryGetConfigurationPath(
        IReadOnlyList<string> arguments,
        out string? path,
        out string? error)
    {
        path = null;
        error = null;
        var indices = Enumerable.Range(0, arguments.Count)
            .Where(index => arguments[index].Equals("--tui", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (indices.Length != 1)
        {
            error = "La opción --tui debe aparecer exactamente una vez y requiere la ruta del TOML.";
            return false;
        }

        var index = indices[0];
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "--tui requiere la ruta del TOML efectivo como argumento siguiente.";
            return false;
        }

        path = arguments[index + 1];
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "--tui requiere una ruta TOML no vacía.";
            return false;
        }
        return true;
    }
}
