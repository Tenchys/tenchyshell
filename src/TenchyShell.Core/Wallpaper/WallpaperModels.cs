namespace TenchyShell.Core.Wallpaper;

public sealed record WallpaperEntry(string Path, string Name);

public sealed record WallpaperCatalog(IReadOnlyList<WallpaperEntry> Items, string? Error);

public sealed record WallpaperOperationResult(bool Succeeded, string? Error)
{
    public static WallpaperOperationResult Success() => new(true, null);
    public static WallpaperOperationResult Failure(string error) => new(false, error);
}

public interface IWallpaperService : IDisposable
{
    WallpaperCatalog GetCatalog();
    WallpaperOperationResult Apply(string path);
    WallpaperOperationResult RestorePrevious();
}
