using System.Diagnostics;
using System.Text;
using TenchyShell.Core.Diagnostics;

namespace TenchyShell.Core.Logging;

public sealed class FileLogger : ILogger
{
    public const long DefaultMaximumBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(14);
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);

    private readonly object syncRoot = new();
    private readonly string logFilePath;
    private readonly string logFileName;
    private readonly long maximumBytes;
    private readonly TimeSpan maximumAge;
    private readonly string sessionId;
    private readonly List<double> writeDurations = new();
    private readonly List<double> maintenanceDurations = new();
    private long logCalls;
    private long writeFailures;
    private long maintenanceCalls;
    private long archiveScans;
    private long archiveFilesSeen;
    private long archiveFilesDeleted;
    private long rotationsByDay;
    private long rotationsBySize;
    private long contentionCount;
    private double contentionWaitMilliseconds;
    private long bytesWritten;
    private long archiveCountAtEnd;
    private DateTime activeLogDate;
    private long activeFileBytes;
    private DateTimeOffset lastMaintenanceAt;
    private bool activeFileStateKnown;

    public bool ConsoleEnabled { get; set; }

    /// <summary>
    /// Habilita las métricas agregadas del logger. Debe activarse únicamente
    /// cuando la configuración opt-in de benchmark ya fue cargada.
    /// </summary>
    public bool MetricsEnabled { get; set; }

    public FileLogger(
        string? logDirectory = null,
        string logFileName = "tenchyshell.log",
        long maximumBytes = DefaultMaximumBytes,
        TimeSpan? maximumAge = null,
        string? sessionId = null)
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
        this.sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        if (this.maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        logFilePath = Path.Combine(directory, logFileName);
        lock (syncRoot)
        {
            MaintainFiles(DateTimeOffset.Now);
        }
    }

    public string LogFilePath => logFilePath;

    public string SessionId => sessionId;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    public FileLoggerMetrics GetMetricsSnapshot()
    {
        lock (syncRoot)
        {
            if (!MetricsEnabled)
            {
                return new FileLoggerMetrics(
                    LogCalls: 0,
                    WriteFailures: 0,
                    MaintenanceCalls: 0,
                    ArchiveScans: 0,
                    ArchiveFilesSeen: 0,
                    ArchiveFilesDeleted: 0,
                    RotationsByDay: 0,
                    RotationsBySize: 0,
                    ContentionCount: 0,
                    ContentionWaitMilliseconds: 0,
                    WriteDurationP95Milliseconds: null,
                    MaintenanceDurationP95Milliseconds: null,
                    BytesWritten: 0,
                    ArchiveCountAtEnd: 0);
            }

            return new FileLoggerMetrics(
                LogCalls: logCalls,
                WriteFailures: writeFailures,
                MaintenanceCalls: maintenanceCalls,
                ArchiveScans: archiveScans,
                ArchiveFilesSeen: archiveFilesSeen,
                ArchiveFilesDeleted: archiveFilesDeleted,
                RotationsByDay: rotationsByDay,
                RotationsBySize: rotationsBySize,
                ContentionCount: contentionCount,
                ContentionWaitMilliseconds: contentionWaitMilliseconds,
                WriteDurationP95Milliseconds: GetP95(writeDurations),
                MaintenanceDurationP95Milliseconds: GetP95(maintenanceDurations),
                BytesWritten: bytesWritten,
                ArchiveCountAtEnd: archiveCountAtEnd);
        }
    }

    private static string GetDefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TenchyShell",
        "logs");

    private void Write(string level, string message)
    {
        // Conserva el prefijo histórico para que los resumidores puedan
        // identificar WARN/ERROR/FATAL sin conocer la versión del logger.
        var line = $"{DateTimeOffset.Now:O} [{level}] {message} [sessionId={sessionId}]{Environment.NewLine}";
        // Capturar la opción una vez evita mezclar una escritura iniciada con
        // métricas deshabilitadas con un cambio concurrente de configuración.
        var metricsEnabled = MetricsEnabled;
        var lineBytes = metricsEnabled ? Encoding.UTF8.GetByteCount(line) : 0;
        var writeStartedAt = metricsEnabled ? Stopwatch.GetTimestamp() : 0;
        var lockTaken = false;

        try
        {
            if (metricsEnabled)
            {
                lockTaken = Monitor.TryEnter(syncRoot);
                var wasContended = !lockTaken;
                if (wasContended)
                {
                    var waitStartedAt = Stopwatch.GetTimestamp();
                    Monitor.Enter(syncRoot, ref lockTaken);
                    contentionWaitMilliseconds += ElapsedMilliseconds(waitStartedAt);
                    contentionCount++;
                }

                logCalls++;
            }
            else
            {
                Monitor.Enter(syncRoot, ref lockTaken);
            }

            var now = DateTimeOffset.Now;
            MaintainIfNeeded(now);
            try
            {
                File.AppendAllText(logFilePath, line);
                if (activeFileStateKnown) activeFileBytes += lineBytes == 0 ? Encoding.UTF8.GetByteCount(line) : lineBytes;
                bytesWritten += lineBytes;
                if (ConsoleEnabled)
                {
                    if (string.Equals(level, "ERROR", StringComparison.Ordinal)) Console.Error.Write(line);
                    else Console.Out.Write(line);
                }
            }
            catch (IOException)
            {
                if (metricsEnabled) writeFailures++;
                // El diagnóstico no puede derribar la sesión si el archivo está bloqueado.
            }
            catch (UnauthorizedAccessException)
            {
                if (metricsEnabled) writeFailures++;
                // La sesión continúa aunque Windows rechace temporalmente el log.
            }
        }
        finally
        {
            if (metricsEnabled)
            {
                // Las muestras comparten la misma sincronizaciÃ³n que el resto
                // del estado para que escritores concurrentes no muten List
                // fuera del lock.
                writeDurations.Add(ElapsedMilliseconds(writeStartedAt));
            }

            if (lockTaken) Monitor.Exit(syncRoot);
        }
    }

    private void MaintainIfNeeded(DateTimeOffset now)
    {
        var dueToRotation = !activeFileStateKnown ||
                            activeFileBytes >= maximumBytes ||
                            activeLogDate < now.LocalDateTime.Date;
        var dueToCadence = now - lastMaintenanceAt >= MaintenanceInterval;
        if (dueToRotation || dueToCadence) MaintainFiles(now);
    }

    private void MaintainFiles(DateTimeOffset now)
    {
        var maintenanceStartedAt = MetricsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (MetricsEnabled) maintenanceCalls++;

        try
        {
            if (File.Exists(logFilePath))
            {
                var info = new FileInfo(logFilePath);
                var isOlderThanToday = info.LastWriteTime.Date < now.LocalDateTime.Date;
                var isAtMaximumSize = info.Length >= maximumBytes;
                if (isAtMaximumSize || isOlderThanToday)
                {
                    var archivePath = CreateArchivePath(now);
                    try
                    {
                        File.Move(logFilePath, archivePath);
                        if (MetricsEnabled && isAtMaximumSize) rotationsBySize++;
                        if (MetricsEnabled && isOlderThanToday) rotationsByDay++;
                    }
                    catch (IOException)
                    {
                        // Un proceso externo puede mantener el archivo abierto;
                        // se conserva el log activo y se reintenta más adelante.
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
        finally
        {
            lastMaintenanceAt = now;
            RefreshActiveFileState(now);
            if (MetricsEnabled) maintenanceDurations.Add(ElapsedMilliseconds(maintenanceStartedAt));
        }
    }

    private void RefreshActiveFileState(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(logFilePath))
            {
                var info = new FileInfo(logFilePath);
                activeFileBytes = info.Length;
                activeLogDate = info.LastWriteTime.Date;
            }
            else
            {
                activeFileBytes = 0;
                activeLogDate = now.LocalDateTime.Date;
            }

            activeFileStateKnown = true;
        }
        catch (IOException)
        {
            activeFileStateKnown = false;
        }
        catch (UnauthorizedAccessException)
        {
            activeFileStateKnown = false;
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
        if (MetricsEnabled)
        {
            archiveScans++;
            archiveFilesSeen += archives.Count;
        }
        var cutoff = now.LocalDateTime - maximumAge;
        var totalBytes = File.Exists(logFilePath) ? new FileInfo(logFilePath).Length : 0;
        var remainingArchives = archives.Count;

        foreach (var archive in archives)
        {
            try
            {
                var shouldDelete = archive.LastWriteTime < cutoff || totalBytes + archive.Length > maximumBytes;
                if (shouldDelete)
                {
                    archive.Delete();
                    if (MetricsEnabled) archiveFilesDeleted++;
                    remainingArchives--;
                    // Sólo ajustar el acumulado después de borrar. Si el
                    // archivo está bloqueado, debe seguir contando para que el
                    // siguiente mantenimiento vuelva a intentarlo.
                }
                else
                {
                    totalBytes += archive.Length;
                }
            }
            catch (FileNotFoundException)
            {
                remainingArchives--;
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

        archiveCountAtEnd = Math.Max(0, remainingArchives);
    }

    private static double ElapsedMilliseconds(long startedAt) =>
        (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;

    private static double? GetP95(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var ordered = values.OrderBy(value => value).ToArray();
        var index = Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * 0.95d) - 1);
        return ordered[index];
    }
}
