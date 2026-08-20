using System.Text;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Wallpaper;

namespace TenchyShell.Win32;

public sealed class WallpaperService : IWallpaperService
{
    private readonly WallpaperConfiguration configuration;
    private readonly IWallpaperStateStore stateStore;
    private readonly ILogger logger;
    private readonly WallpaperSurface surface = new();
    private string? previousWallpaper;

    public WallpaperService(WallpaperConfiguration configuration, IWallpaperStateStore stateStore, ILogger logger)
    {
        this.configuration = configuration;
        this.stateStore = stateStore;
        this.logger = logger;
    }

    public WallpaperCatalog GetCatalog()
    {
        try
        {
            var folder = ResolveFolder();
            if (!Directory.Exists(folder)) return new(Array.Empty<WallpaperEntry>(), $"No existe la carpeta de fondos: {folder}");

            var extensions = new HashSet<string>(configuration.Extensions, StringComparer.OrdinalIgnoreCase);
            var items = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Select(path => new WallpaperEntry(path, Path.GetFileName(path)))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(items, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(Array.Empty<WallpaperEntry>(), $"No se pudieron leer los fondos: {exception.Message}");
        }
    }

    public WallpaperOperationResult Apply(string path)
    {
        try
        {
            var absolutePath = Path.GetFullPath(path);
            if (!File.Exists(absolutePath)) return WallpaperOperationResult.Failure("El fondo seleccionado ya no existe.");
            previousWallpaper ??= GetCurrentWallpaper();
            if (!surface.SetImage(absolutePath, out var surfaceError))
            {
                return WallpaperOperationResult.Failure(surfaceError ?? "No se pudo mostrar el fondo.");
            }

            var stateResult = stateStore.Save(absolutePath);
            if (!stateResult.Succeeded)
            {
                logger.Error(stateResult.Error ?? "No se pudo guardar el último wallpaper seleccionado.");
            }

            return WallpaperOperationResult.Success();
        }
        catch (Exception exception)
        {
            return WallpaperOperationResult.Failure($"No se pudo aplicar el fondo: {exception.Message}");
        }
    }

    public WallpaperOperationResult RestorePrevious()
    {
        if (string.IsNullOrWhiteSpace(previousWallpaper)) return WallpaperOperationResult.Failure("No hay un fondo anterior guardado.");
        var result = Apply(previousWallpaper);
        if (result.Succeeded) previousWallpaper = null;
        return result;
    }

    public void Dispose() => surface.Dispose();

    private string ResolveFolder() => string.IsNullOrWhiteSpace(configuration.Folder)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        : Environment.ExpandEnvironmentVariables(configuration.Folder);

    private static string GetCurrentWallpaper()
    {
        var buffer = new StringBuilder(1024);
        return NativeMethods.SystemParametersInfo(
            NativeMethods.SPI_GETDESKWALLPAPER,
            (uint)buffer.Capacity,
            buffer,
            0) ? buffer.ToString() : string.Empty;
    }

}
