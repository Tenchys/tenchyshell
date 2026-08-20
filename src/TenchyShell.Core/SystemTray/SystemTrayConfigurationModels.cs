namespace TenchyShell.Core.SystemTray;

public sealed class SystemTrayItemConfiguration
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;

    public string Icon { get; init; } = string.Empty;

    public string DefaultIcon { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public int IntervalMilliseconds { get; init; } = 5000;

    public int TimeoutMilliseconds { get; init; } = 1500;
}

public sealed class SystemTrayActionConfiguration
{
    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

public sealed record SystemTrayScriptOutput(
    string? Text,
    string? Tooltip,
    string? Icon,
    string? State,
    string? Action);

public sealed record SystemTrayItemSnapshot(
    string Id,
    string Title,
    string Text,
    string Tooltip,
    string Icon,
    string State,
    string? Action,
    bool IsStale,
    string? Error);
