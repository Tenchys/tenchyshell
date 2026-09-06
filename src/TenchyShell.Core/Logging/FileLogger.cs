namespace TenchyShell.Core.Logging;

public sealed class FileLogger : ILogger
{
    public const long DefaultMaximumBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(14);

    private readonly object syncRoot = new();
    private readonly string logFilePath;
    private readonly string logFileName;
    private readonly long maximumBytes;
    private readonly TimeSpan maximumAge;

    public FileLogger(
        string? logDirectory = null,
        string logFileName = "tenchyshell.log",
        long maximumBytes = DefaultMaximumBytes,
        TimeSpan? maximumAge = null)
    {
        var directory = logDirectory ?? GetDefaultLogDirectory();
        if (string.IsNullOrWhiteSpace(logFileName) ||
            !string.Equals(Path.GetFileName(logFileName), logFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("El nombre del log debe ser un archivo simple.", nameof(logFileName));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);
        Directory.CreateDirectory(directory);
        this.logFileName = logFileName;
        this.maximumBytes = maximumBytes;
        this.maximumAge = maximumAge ?? DefaultMaximumAge;
        if (this.maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        logFilePath = Path.Combine(directory, logFileName);
        lock (syncRoot)
        {
            MaintainFiles(DateTimeOffset.Now);
        }
    }

    public string LogFilePath => logFilePath;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static string GetDefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TenchyShell",
        "logs");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";

        lock (syncRoot)
        {
            MaintainFiles(DateTimeOffset.Now);
            try
            {
                File.AppendAllText(logFilePath, line);
            }
            catch (IOException)
            {
                // El diagnóstico no puede derribar la sesión si el archivo está bloqueado.
            }
            catch (UnauthorizedAccessException)
            {
                // La sesión continúa aunque Windows rechace temporalmente el log.
            }
        }
    }

    private void MaintainFiles(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(logFilePath))
            {
                var info = new FileInfo(logFilePath);
                var isOlderThanToday = info.LastWriteTime.Date < now.LocalDateTime.Date;
                if (info.Length >= maximumBytes || isOlderThanToday)
                {
                    var archivePath = CreateArchivePath(now);
                    try
                    {
                        File.Move(logFilePath, archivePath);
                    }
                    catch (IOException)
                    {
                        // Un proceso externo puede mantener el archivo abierto;
                        // se conserva el log activo y se reintenta en la siguiente escritura.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // La sesión no debe dejar de arrancar por mantenimiento de logs.
                    }
                }
            }

            PruneArchives(now);
        }
        catch (IOException)
        {
            // La rotación es auxiliar y no puede interrumpir el shell.
        }
        catch (UnauthorizedAccessException)
        {
            // La rotación es auxiliar y no puede interrumpir el shell.
        }
    }

    private string CreateArchivePath(DateTimeOffset now)
    {
        var stem = Path.GetFileNameWithoutExtension(logFileName);
        var extension = Path.GetExtension(logFileName);
        var timestamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmssfff");
        var path = Path.Combine(Path.GetDirectoryName(logFilePath)!, $"{stem}-{timestamp}{extension}");
        var suffix = 0;
        while (File.Exists(path))
        {
            suffix++;
            path = Path.Combine(Path.GetDirectoryName(logFilePath)!, $"{stem}-{timestamp}-{suffix}{extension}");
        }

        return path;
    }

    private void PruneArchives(DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(logFilePath)!;
        var stem = Path.GetFileNameWithoutExtension(logFileName);
        var extension = Path.GetExtension(logFileName);
        var pattern = $"{stem}-*{extension}";
        var archives = Directory.EnumerateFiles(directory, pattern)
            .Where(path => !string.Equals(path, logFilePath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();
        var cutoff = now.LocalDateTime - maximumAge;
        var totalBytes = File.Exists(logFilePath) ? new FileInfo(logFilePath).Length : 0;

        foreach (var archive in archives)
        {
            try
            {
                var shouldDelete = archive.LastWriteTime < cutoff || totalBytes + archive.Length > maximumBytes;
                if (shouldDelete)
                {
                    archive.Delete();
                    // Sólo ajustar el acumulado después de borrar. Si el
                    // archivo está bloqueado, debe seguir contando para que
                    // el siguiente mantenimiento vuelva a intentarlo.
                }
                else
                {
                    totalBytes += archive.Length;
                }
            }
            catch (FileNotFoundException)
            {
                // Otro proceso pudo eliminarlo entre EnumerateFiles y Delete.
            }
            catch (DirectoryNotFoundException)
            {
                // El directorio puede desaparecer durante el cierre o una
                // limpieza externa; el siguiente mantenimiento lo reintentará.
            }
            catch (UnauthorizedAccessException)
            {
                // Un archivo inaccesible no debe impedir limpiar los demás.
            }
            catch (IOException)
            {
                // Un archivo bloqueado no debe impedir limpiar los demás.
            }
        }
    }
}
