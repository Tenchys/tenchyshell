using System.Text.Json;

namespace TenchyShell.Core.Wallpaper;

public sealed record WallpaperStateLoadResult(string? LastWallpaperPath, string? Error)
{
    public bool HasSavedWallpaper => !string.IsNullOrWhiteSpace(LastWallpaperPath);
}

public interface IWallpaperStateStore
{
    WallpaperStateLoadResult Load();
    WallpaperOperationResult Save(string wallpaperPath);
}

/// <summary>Guarda solamente la ruta del último fondo aplicado por TenchyShell.</summary>
public sealed class WallpaperStateStore : IWallpaperStateStore
{
    private const string StateFileName = "wallpaper.json";
    private readonly string stateFilePath;

    public WallpaperStateStore(string? stateFilePath = null)
    {
        this.stateFilePath = stateFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TenchyShell",
            "state",
            StateFileName);
    }

    public WallpaperStateLoadResult Load()
    {
        if (!File.Exists(stateFilePath)) return new(null, null);

        try
        {
            var state = JsonSerializer.Deserialize<PersistedWallpaperState>(File.ReadAllText(stateFilePath));
            if (string.IsNullOrWhiteSpace(state?.LastWallpaperPath))
            {
                return new(null, "El estado del wallpaper no contiene una ruta válida.");
            }

            return new(state.LastWallpaperPath, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, $"No se pudo leer el estado del wallpaper: {exception.Message}");
        }
    }

    public WallpaperOperationResult Save(string wallpaperPath)
    {
        if (string.IsNullOrWhiteSpace(wallpaperPath)) return WallpaperOperationResult.Failure("La ruta del wallpaper está vacía.");

        var temporaryPath = string.Empty;
        try
        {
            var absolutePath = Path.GetFullPath(wallpaperPath);
            var directory = Path.GetDirectoryName(stateFilePath);
            if (string.IsNullOrWhiteSpace(directory)) return WallpaperOperationResult.Failure("La ruta del estado del wallpaper no tiene directorio.");

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"{StateFileName}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(new PersistedWallpaperState(absolutePath));
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, stateFilePath, overwrite: true);
            return WallpaperOperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return WallpaperOperationResult.Failure($"No se pudo guardar el estado del wallpaper: {exception.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                    // El archivo temporal nunca sustituye el estado válido anterior.
                }
            }
        }
    }

    private sealed record PersistedWallpaperState(string LastWallpaperPath);
}
