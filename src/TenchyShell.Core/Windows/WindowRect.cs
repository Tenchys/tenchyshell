namespace TenchyShell.Core.Windows;

public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
