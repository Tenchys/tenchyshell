using Tomlyn;
using Tomlyn.Model;

namespace TenchyShell.Core.Yazi;

public sealed class YaziShortcutReader
{
    public const string DefaultPresetVersion = "shipped";

    public YaziShortcutReadResult Read(string? configHome = null, string? appData = null)
    {
        YaziShortcutPath path;
        try
        {
            path = YaziConfigPathResolver.Resolve(configHome ?? Environment.GetEnvironmentVariable("YAZI_CONFIG_HOME"), appData);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            var invalid = configHome ?? Environment.GetEnvironmentVariable("YAZI_CONFIG_HOME") ?? string.Empty;
            return new(new YaziShortcutPath(invalid, invalid, false), YaziShortcutLoadStatus.Invalid, Array.Empty<YaziShortcut>(), new[] { $"Ruta de configuración de Yazi inválida: {exception.Message}" });
        }
        if (!path.Exists)
        {
            var preset = ReadText(DefaultPresetText, path, YaziShortcutSource.DefaultPreset, DefaultPresetVersion);
            return preset with { Status = preset.Errors.Count == 0 ? YaziShortcutLoadStatus.DefaultPreset : YaziShortcutLoadStatus.Partial };
        }
        try
        {
            return ReadText(File.ReadAllText(path.FilePath), path, YaziShortcutSource.UserConfiguration, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(path, YaziShortcutLoadStatus.Unreadable, Array.Empty<YaziShortcut>(), new[] { $"No se pudo leer keymap.toml: {exception.Message}" }); }
        catch (Exception exception)
        { return new(path, YaziShortcutLoadStatus.Malformed, Array.Empty<YaziShortcut>(), new[] { $"keymap.toml no es válido: {exception.Message}" }); }
    }

    private static YaziShortcutReadResult ReadText(string text, YaziShortcutPath path, YaziShortcutSource source, string? presetVersion)
    {
        var root = Toml.ToModel<TomlTable>(text);
        var shortcuts = new List<YaziShortcut>();
        var errors = new List<string>();
        foreach (var context in root)
        {
            if (context.Value is not TomlTable table) continue;
            ReadLayer(table, context.Key, "prepend_keymap", shortcuts, errors);
            ReadLayer(table, context.Key, "keymap", shortcuts, errors);
            ReadLayer(table, context.Key, "append_keymap", shortcuts, errors);
        }
        var effective = shortcuts.GroupBy(s => $"{s.Context}\u001f{s.Sequence}", StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        return new(path, source == YaziShortcutSource.DefaultPreset ? YaziShortcutLoadStatus.DefaultPreset : errors.Count == 0 ? YaziShortcutLoadStatus.Loaded : YaziShortcutLoadStatus.Partial, effective, errors, source, presetVersion);
    }

    private static void ReadLayer(TomlTable context, string contextName, string layer, ICollection<YaziShortcut> output, ICollection<string> errors)
    {
        if (!context.TryGetValue(layer, out var raw)) return;
        var entries = raw switch
        {
            TomlTableArray tableArray => tableArray.Cast<object?>(),
            TomlArray array => array.Cast<object?>(),
            _ => null
        };
        if (entries is null) { errors.Add($"{contextName}.{layer} no es una lista de atajos."); return; }
        foreach (var rawEntry in entries)
        {
            if (rawEntry is not TomlTable entry) { errors.Add($"{contextName}.{layer} contiene una entrada desconocida."); continue; }
            var keys = ReadKeys(entry, "on");
            var action = ReadValue(entry, "run") ?? ReadValue(entry, "action");
            if (keys.Count == 0 || string.IsNullOrWhiteSpace(action)) { errors.Add($"{contextName}.{layer} contiene una entrada incompleta."); continue; }
            output.Add(new YaziShortcut(contextName, layer, string.Join(" ", keys), action, ReadValue(entry, "desc") ?? ReadValue(entry, "description") ?? string.Empty));
        }
    }
    private static List<string> ReadKeys(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var raw)) return new();
        if (raw is string text) return new() { text };
        if (raw is TomlArray array) return array.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return new();
    }
    private static string? ReadText(TomlTable table, string key) => table.TryGetValue(key, out var raw) && raw is string text ? text : null;

    private static string? ReadValue(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var raw)) return null;
        if (raw is string text) return text;
        if (raw is TomlArray array) return string.Join(" ; ", array.OfType<string>());
        return null;
    }

    private const string DefaultPresetText = """
        [mgr]
        keymap = [
          { on = "<Esc>", run = "escape", desc = "Exit visual mode, clear selection, or cancel search" },
          { on = "q", run = "quit", desc = "Quit the process" },
          { on = "k", run = "arrow prev", desc = "Previous file" },
          { on = "j", run = "arrow next", desc = "Next file" },
          { on = [ "g", "g" ], run = "arrow top", desc = "Go to top" },
          { on = "h", run = "leave", desc = "Back to the parent directory" },
          { on = "l", run = "enter", desc = "Enter the child directory" },
          { on = "o", run = "open", desc = "Open selected files" },
          { on = "y", run = "yank", desc = "Yank selected files (copy)" },
          { on = "p", run = "paste", desc = "Paste yanked files" },
          { on = "d", run = "remove", desc = "Trash selected files" },
          { on = "a", run = "create", desc = "Create a file" },
          { on = "r", run = "rename", desc = "Rename selected file(s)" },
          { on = ".", run = "hidden toggle", desc = "Toggle hidden files" },
          { on = "~", run = "help", desc = "Open help" }
        ]
        [tasks]
        keymap = [ { on = "q", run = "close", desc = "Close task manager" } ]
        [spot]
        keymap = [ { on = "q", run = "close", desc = "Close the spot" } ]
        [pick]
        keymap = [ { on = "q", run = "close", desc = "Cancel pick" } ]
        [input]
        keymap = [ { on = "<Enter>", run = "close --submit", desc = "Submit input" } ]
        [confirm]
        keymap = [ { on = "<Enter>", run = "close --submit", desc = "Submit confirmation" } ]
        [cmp]
        keymap = [ { on = "<Tab>", run = "close --submit", desc = "Submit completion" } ]
        [help]
        keymap = [ { on = "q", run = "escape", desc = "Close help" } ]
        """;
}
