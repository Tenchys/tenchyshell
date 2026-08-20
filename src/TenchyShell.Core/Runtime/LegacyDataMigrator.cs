using System.Security.Cryptography;
using System.Text.Json;

namespace TenchyShell.Core.Runtime;

public enum MigrationItemStatus
{
    Missing,
    Copied,
    AlreadyPresent,
    Conflict,
    Invalid,
    Error
}

public sealed record MigrationItemResult(
    string SourceRelativePath,
    string TargetRelativePath,
    MigrationItemStatus Status,
    string? Error = null);

public sealed record LegacyMigrationResult(IReadOnlyList<MigrationItemResult> Items)
{
    public bool HasErrors => Items.Any(item => item.Status is MigrationItemStatus.Conflict or MigrationItemStatus.Invalid or MigrationItemStatus.Error);
}

/// <summary>Copies known MinimalShell user files without deleting or overwriting either side.</summary>
public static class LegacyDataMigrator
{
    private sealed record FileMapping(string Source, string Target, Func<string, bool>? Validator = null);

    private static readonly FileMapping[] Mappings =
    {
        new("MinimalShell.toml", "TenchyShell.toml"),
        new("MinimalShell.example.toml", "TenchyShell.example.toml"),
        new("MinimalShell.without-explorer.example.toml", "TenchyShell.without-explorer.example.toml"),
        new(Path.Combine("state", "wallpaper.json"), Path.Combine("state", "wallpaper.json"), IsValidWallpaperState),
        new(Path.Combine("logs", "minimalshell.log"), Path.Combine("logs", "minimalshell-legacy.log"))
    };

    public static LegacyMigrationResult MigrateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Migrate(
            Path.Combine(localApplicationData, "MinimalShell"),
            Path.Combine(localApplicationData, "TenchyShell"));
    }

    public static LegacyMigrationResult Migrate(string sourceDirectory, string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory);
        var results = new List<MigrationItemResult>();

        foreach (var mapping in Mappings)
        {
            var source = GetContainedPath(sourceRoot, mapping.Source);
            var target = GetContainedPath(targetRoot, mapping.Target);
            if (!File.Exists(source))
            {
                results.Add(new(mapping.Source, mapping.Target, MigrationItemStatus.Missing));
                continue;
            }

            try
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                {
                    results.Add(new(mapping.Source, mapping.Target, MigrationItemStatus.Invalid, "El archivo heredado es un enlace o reparse point."));
                    continue;
                }
                if (mapping.Validator is not null && !mapping.Validator(source))
                {
                    results.Add(new(mapping.Source, mapping.Target, MigrationItemStatus.Invalid, "El contenido heredado no es válido."));
                    continue;
                }
                if (File.Exists(target))
                {
                    var matches = FilesMatch(source, target);
                    results.Add(new(
                        mapping.Source,
                        mapping.Target,
                        matches ? MigrationItemStatus.AlreadyPresent : MigrationItemStatus.Conflict,
                        matches ? null : "El destino ya existe con contenido diferente; no se sobrescribió."));
                    continue;
                }

                var targetParent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetParent);
                var temporaryPath = Path.Combine(targetParent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(source, temporaryPath, overwrite: false);
                    File.Move(temporaryPath, target, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                results.Add(new(mapping.Source, mapping.Target, MigrationItemStatus.Copied));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                results.Add(new(mapping.Source, mapping.Target, MigrationItemStatus.Error, exception.Message));
            }
        }

        return new LegacyMigrationResult(results);
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La ruta de migración sale del directorio permitido.");
        }
        return path;
    }

    private static bool FilesMatch(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length) return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
    }

    private static bool IsValidWallpaperState(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("LastWallpaperPath", out var property) &&
                   property.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(property.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
