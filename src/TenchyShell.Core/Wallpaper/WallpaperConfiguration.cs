namespace TenchyShell.Core.Wallpaper;

public sealed class WallpaperConfiguration
{
    public bool Enabled { get; init; } = true;
    public string Folder { get; init; } = string.Empty;
    public IReadOnlyList<string> Extensions { get; init; } = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    public string Monitor { get; init; } = "all";
}
