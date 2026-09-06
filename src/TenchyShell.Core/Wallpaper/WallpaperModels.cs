namespace TenchyShell.Core.Wallpaper;

public sealed record WallpaperEntry(string Path, string Name);

public sealed record WallpaperCatalog(IReadOnlyList<WallpaperEntry> Items, string? Error);

/// <summary>Calcula el rectángulo de una imagen que cubre el escritorio sin deformarla.</summary>
public static class WallpaperImageLayout
{
    public static WallpaperPixelRect CalculateCover(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));

        var scale = Math.Max((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
        var width = Math.Max(targetWidth, (int)Math.Ceiling(sourceWidth * scale));
        var height = Math.Max(targetHeight, (int)Math.Ceiling(sourceHeight * scale));
        return new((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }
}

public readonly record struct WallpaperPixelRect(int X, int Y, int Width, int Height);

public sealed record WallpaperOperationResult(bool Succeeded, string? Error)
{
    public static WallpaperOperationResult Success() => new(true, null);
    public static WallpaperOperationResult Failure(string error) => new(false, error);
}

public interface IWallpaperService : IDisposable
{
    WallpaperCatalog GetCatalog();
    WallpaperOperationResult Apply(string path);
    WallpaperOperationResult ApplyNoBackground();
    WallpaperOperationResult RestorePrevious();
}
