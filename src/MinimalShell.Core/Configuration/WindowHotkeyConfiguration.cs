namespace MinimalShell.Core.Configuration;

public sealed class WindowHotkeyConfiguration
{
    public string MoveLeft { get; init; } = "Ctrl+Alt+Left";

    public string MoveRight { get; init; } = "Ctrl+Alt+Right";

    public string MoveUp { get; init; } = "Ctrl+Alt+Up";

    public string MoveDown { get; init; } = "Ctrl+Alt+Down";

    public string ResizeGrow { get; init; } = "Ctrl+Alt+Shift+Right";

    public string ResizeShrink { get; init; } = "Ctrl+Alt+Shift+Left";

    public string Maximize { get; init; } = "Ctrl+Alt+M";

    public string Restore { get; init; } = "Ctrl+Alt+R";

    public string Focus { get; init; } = "Ctrl+Alt+F";
}
