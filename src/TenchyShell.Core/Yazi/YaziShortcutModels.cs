namespace TenchyShell.Core.Yazi;

public enum YaziShortcutLoadStatus { Loaded, Missing, Partial, Invalid, Unreadable, Malformed, DefaultPreset }
public enum YaziShortcutSource { UserConfiguration, DefaultPreset }
public sealed record YaziShortcutPath(string Directory, string FilePath, bool Exists);
public sealed record YaziShortcut(string Context, string Layer, string Sequence, string Action, string Description);
public sealed record YaziShortcutReadResult(
    YaziShortcutPath Path,
    YaziShortcutLoadStatus Status,
    IReadOnlyList<YaziShortcut> Shortcuts,
    IReadOnlyList<string> Errors,
    YaziShortcutSource Source = YaziShortcutSource.UserConfiguration,
    string? PresetVersion = null);

public static class YaziConfigPathResolver
{
    public static YaziShortcutPath Resolve(string? configHome = null, string? appData = null)
    {
        var home = !string.IsNullOrWhiteSpace(configHome)
            ? configHome!
            : Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yazi", "config");
        var file = Path.Combine(home, "keymap.toml");
        return new YaziShortcutPath(home, file, File.Exists(file));
    }
}
