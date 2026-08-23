namespace TenchyShell.Core.Configuration;

/// <summary>
/// Resuelve la configuración por usuario sin acoplar el dominio al directorio
/// desde el que se inició el ejecutable.
/// </summary>
public static class ConfigurationPathResolver
{
    public const string DirectoryName = "tenchyshell";
    public const string FileName = "config.toml";

    public static string GetDefaultPath(string? userProfileDirectory = null)
    {
        var profile = string.IsNullOrWhiteSpace(userProfileDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileDirectory;

        return Path.Combine(profile, ".config", DirectoryName, FileName);
    }

    public static string? Resolve(string? explicitPath) =>
        Resolve(explicitPath, GetDefaultPath(), File.Exists);

    public static string? Resolve(string? explicitPath, string defaultPath, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        return fileExists(defaultPath) ? defaultPath : null;
    }
}
