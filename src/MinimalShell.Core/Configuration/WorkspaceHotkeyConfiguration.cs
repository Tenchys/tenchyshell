namespace MinimalShell.Core.Configuration;

public sealed class WorkspaceHotkeyConfiguration
{
    public IReadOnlyList<string> Switch { get; init; } = CreateDefaults("Ctrl+Alt");

    public IReadOnlyList<string> Move { get; init; } = CreateDefaults("Ctrl+Alt+Shift");

    public static WorkspaceHotkeyConfiguration CreateDefault() => new();

    private static IReadOnlyList<string> CreateDefaults(string modifiers) =>
        Enumerable.Range(1, 9).Select(index => $"{modifiers}+{index}").ToArray();
}
