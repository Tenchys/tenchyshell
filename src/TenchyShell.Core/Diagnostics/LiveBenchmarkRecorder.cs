using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Logging;

namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Registro local y opt-in para observar una sesión real. No controla procesos
/// ni modifica el comportamiento del shell.
/// </summary>
public sealed class LiveBenchmarkRecorder : IDisposable
{
    private static readonly ConcurrentDictionary<string, object> fileWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly BenchmarkConfiguration configuration;
    private readonly ILogger logger;
    private readonly string directory;
    private readonly bool suppressed;
    private readonly object syncRoot = new();
    private readonly string sessionId;
    private readonly Dictionary<string, DateTimeOffset> lastSampledEventAt = new(StringComparer.Ordinal);
    private Timer? timer;
    private bool disposed;
    private bool sessionEnded;

    public LiveBenchmarkRecorder(
        BenchmarkConfiguration configuration,
        ILogger logger,
        string? rootDirectory = null,
        bool suppressed = false,
        string? sessionId = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.suppressed = suppressed;
        this.sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TenchyShell", "benchmarks", "live");
    }

    public string DirectoryPath => directory;

    public string SessionId => sessionId;

    public bool IsEnabled => configuration.Enabled && !suppressed && !disposed;

    public void Start()
    {
        if (!IsEnabled) return;
        try
        {
            Directory.CreateDirectory(directory);
            Prune();
            Record("session_started", new { profile = configuration.CaptureProfile });
            var interval = TimeSpan.FromSeconds(configuration.SampleIntervalSeconds);
            timer = new Timer(_ => CaptureSample(), null, interval, interval);
        }
        catch (Exception exception)
        {
            SafeLogError("No se pudo iniciar el benchmark de sesión; TenchyShell continuará activo.", exception);
        }
    }

    public void Record(string eventName, object? details = null)
    {
        if (!IsEnabled) return;
        try
        {
            Write(new { timestamp = DateTimeOffset.UtcNow, sessionId, type = "event", name = eventName, details });
        }
        catch (Exception exception)
        {
            SafeLogError($"No se pudo registrar el evento de benchmark '{eventName}'.", exception);
        }
    }

    /// <summary>
    /// Registra un evento agregado como máximo una vez por intervalo de muestra.
    /// Evita que una ruta de refresco frecuente altere el benchmark que observa.
    /// </summary>
    public bool RecordAtMostEverySampleInterval(string eventName, object? details = null)
    {
        if (!IsEnabled) return false;
        try
        {
            lock (syncRoot)
            {
                if (disposed) return false;

                var now = DateTimeOffset.UtcNow;
                var interval = TimeSpan.FromSeconds(configuration.SampleIntervalSeconds);
                if (lastSampledEventAt.TryGetValue(eventName, out var lastRecordedAt) && now - lastRecordedAt < interval)
                {
                    return false;
                }

                WriteUnsafe(new { timestamp = now, sessionId, type = "event", name = eventName, details });
                lastSampledEventAt[eventName] = now;
                return true;
            }
        }
        catch (Exception exception)
        {
            SafeLogError($"No se pudo registrar el evento de benchmark limitado '{eventName}'.", exception);
            return false;
        }
    }

    /// <summary>
    /// Publica solo contadores y recursos agregados de un ejecutor periódico.
    /// Los comandos, argumentos y salida nunca forman parte de este registro.
    /// </summary>
    public void RecordPeriodicExecutionMetrics(PeriodicExecutionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (string.IsNullOrWhiteSpace(metrics.Executor))
        {
            throw new ArgumentException("El ejecutor de métricas es obligatorio.", nameof(metrics));
        }

        Record("periodic_execution_metrics", new
        {
            executor = metrics.Executor,
            processesStarted = Math.Max(0, metrics.ProcessesStarted),
            overlapsAvoided = Math.Max(0, metrics.OverlapsAvoided),
            cancellations = Math.Max(0, metrics.Cancellations),
            timeouts = Math.Max(0, metrics.Timeouts),
            errors = Math.Max(0, metrics.Errors),
            cpuMilliseconds = NonNegative(metrics.CpuMilliseconds),
            readBytes = NonNegative(metrics.ReadBytes),
            writtenBytes = NonNegative(metrics.WrittenBytes),
            peakWorkingSetBytes = NonNegative(metrics.PeakWorkingSetBytes),
            lastValidOutputAgeMilliseconds = Math.Max(0, metrics.LastValidOutputAgeMilliseconds),
            durationMilliseconds = Math.Max(0, metrics.DurationMilliseconds)
        });
    }

    /// <summary>
    /// Publica el agregado seguro del logger de archivos. La fuente debe
    /// entregar únicamente contadores y duraciones; el recorder no acepta ni
    /// serializa detalles libres para este evento.
    /// </summary>
    public void RecordFileLoggerMetrics(FileLoggerMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        Record("file_logger_metrics", new
        {
            logCalls = NonNegative(metrics.LogCalls),
            writeFailures = NonNegative(metrics.WriteFailures),
            maintenanceCalls = NonNegative(metrics.MaintenanceCalls),
            archiveScans = NonNegative(metrics.ArchiveScans),
            archiveFilesSeen = NonNegative(metrics.ArchiveFilesSeen),
            archiveFilesDeleted = NonNegative(metrics.ArchiveFilesDeleted),
            rotationsByDay = NonNegative(metrics.RotationsByDay),
            rotationsBySize = NonNegative(metrics.RotationsBySize),
            contentionCount = NonNegative(metrics.ContentionCount),
            contentionWaitMilliseconds = NonNegative(metrics.ContentionWaitMilliseconds),
            writeDurationP95Milliseconds = NonNegative(metrics.WriteDurationP95Milliseconds),
            maintenanceDurationP95Milliseconds = NonNegative(metrics.MaintenanceDurationP95Milliseconds),
            bytesWritten = NonNegative(metrics.BytesWritten),
            archiveCountAtEnd = NonNegative(metrics.ArchiveCountAtEnd)
        });
    }

    /// <summary>
    /// Publica sólo el agregado del coste de los hooks globales del layout.
    /// Nunca incluye coordenadas, teclas, HWND ni títulos.
    /// </summary>
    public void RecordLayoutHookMetrics(LayoutHookMetricsSnapshot metrics)
    {
        Record("layout_hook_metrics", new
        {
            callbacks = NonNegative(metrics.Callbacks),
            mouseCallbacks = NonNegative(metrics.MouseCallbacks),
            keyboardCallbacks = NonNegative(metrics.KeyboardCallbacks),
            callbacksDuringDrag = NonNegative(metrics.CallbacksDuringDrag),
            overlayUpdatesPublished = NonNegative(metrics.OverlayUpdatesPublished),
            cancellations = NonNegative(metrics.Cancellations),
            errors = NonNegative(metrics.Errors),
            totalDurationMilliseconds = NonNegative(metrics.TotalDurationMilliseconds),
            maximumDurationMilliseconds = NonNegative(metrics.MaximumDurationMilliseconds)
        });
    }

    /// <summary>
    /// Publica sólo contadores agregados del renderizado de la bandeja y limita
    /// el evento al intervalo de muestra configurado.
    /// </summary>
    public void RecordSystemTrayMetrics(SystemTrayMetricsSnapshot metrics)
    {
        RecordAtMostEverySampleInterval("system_tray_metrics", new
        {
            repaints = NonNegative(metrics.Repaints),
            iconLoadsSucceeded = NonNegative(metrics.IconLoadsSucceeded),
            iconLoadsFailed = NonNegative(metrics.IconLoadsFailed),
            totalPaintDurationMilliseconds = NonNegative(metrics.TotalPaintDurationMilliseconds),
            maximumPaintDurationMilliseconds = NonNegative(metrics.MaximumPaintDurationMilliseconds),
            readBytes = NonNegative(metrics.ReadBytes),
            writtenBytes = NonNegative(metrics.WrittenBytes),
            gdiHandlesBefore = NonNegative(metrics.GdiHandlesBefore),
            gdiHandlesAfter = NonNegative(metrics.GdiHandlesAfter)
        });
    }

    /// <summary>Publica solo agregados de operaciones del historial de notificaciones.</summary>
    public void RecordNotificationCenterMetrics(NotificationCenterMetricsSnapshot metrics)
    {
        Record("notification_center_metrics", new
        {
            addCount = NonNegative(metrics.AddCount),
            addTotalMilliseconds = NonNegative(metrics.AddTotalMilliseconds),
            addAllocatedBytes = NonNegative(metrics.AddAllocatedBytes),
            addMaximumMilliseconds = NonNegative(metrics.AddMaximumMilliseconds),
            addMaximumAllocatedBytes = NonNegative(metrics.AddMaximumAllocatedBytes),
            addDurationP95Milliseconds = NonNegative(metrics.AddDurationP95Milliseconds),
            getActiveCount = NonNegative(metrics.GetActiveCount),
            getActiveTotalMilliseconds = NonNegative(metrics.GetActiveTotalMilliseconds),
            getActiveAllocatedBytes = NonNegative(metrics.GetActiveAllocatedBytes),
            getActiveMaximumMilliseconds = NonNegative(metrics.GetActiveMaximumMilliseconds),
            getActiveMaximumAllocatedBytes = NonNegative(metrics.GetActiveMaximumAllocatedBytes),
            getActiveDurationP95Milliseconds = NonNegative(metrics.GetActiveDurationP95Milliseconds),
            removeCount = NonNegative(metrics.RemoveCount),
            removeTotalMilliseconds = NonNegative(metrics.RemoveTotalMilliseconds),
            removeAllocatedBytes = NonNegative(metrics.RemoveAllocatedBytes),
            removeMaximumMilliseconds = NonNegative(metrics.RemoveMaximumMilliseconds),
            removeMaximumAllocatedBytes = NonNegative(metrics.RemoveMaximumAllocatedBytes),
            removeDurationP95Milliseconds = NonNegative(metrics.RemoveDurationP95Milliseconds),
            trimCalls = NonNegative(metrics.TrimCalls),
            trimmedItems = NonNegative(metrics.TrimmedItems)
        });
    }

    /// <summary>Publica solo agregados del pintado de tarjetas y lectura de sus iconos.</summary>
    public void RecordNotificationSurfaceMetrics(NotificationSurfaceMetricsSnapshot metrics)
    {
        Record("notification_surface_metrics", new
        {
            repaints = NonNegative(metrics.Repaints),
            iconDecodeAttempts = NonNegative(metrics.IconDecodeAttempts),
            totalIconDecodeDurationMilliseconds = NonNegative(metrics.TotalIconDecodeDurationMilliseconds),
            maximumIconDecodeDurationMilliseconds = NonNegative(metrics.MaximumIconDecodeDurationMilliseconds),
            totalPaintDurationMilliseconds = NonNegative(metrics.TotalPaintDurationMilliseconds),
            maximumPaintDurationMilliseconds = NonNegative(metrics.MaximumPaintDurationMilliseconds),
            p95PaintDurationMilliseconds = NonNegative(metrics.P95PaintDurationMilliseconds),
            iconBytesPresentedToDecoder = NonNegative(metrics.IconBytesPresentedToDecoder),
            iconBytesWritten = NonNegative(metrics.IconBytesWritten)
        });
    }

    /// <summary>
    /// Publica solo contadores y duraciones agregadas de una enumeraci?n de
    /// red. No incluye SSID, nombres, rutas, HWND, t?tulos ni datos de sesi?n.
    /// </summary>
    public void RecordNetworkEnumerationMetrics(NetworkEnumerationMetricsSnapshot metrics)
    {
        Record("network_enumeration", new
        {
            ethernetInterfaces = NonNegative(metrics.EthernetInterfaces),
            wifiInterfaces = NonNegative(metrics.WifiInterfaces),
            vpnInterfaces = NonNegative(metrics.VpnInterfaces),
            unknownInterfaces = NonNegative(metrics.UnknownInterfaces),
            wifiNetworksBeforeDeduplication = NonNegative(metrics.WifiNetworksBeforeDeduplication),
            uniqueWifiNetworks = NonNegative(metrics.UniqueWifiNetworks),
            maximumNetworksPerSnapshot = NonNegative(metrics.MaximumNetworksPerSnapshot),
            totalDurationMilliseconds = NonNegative(metrics.TotalDurationMilliseconds),
            interfaceEnumerationDurationMilliseconds = NonNegative(metrics.InterfaceEnumerationDurationMilliseconds),
            wifiEnumerationDurationMilliseconds = NonNegative(metrics.WifiEnumerationDurationMilliseconds),
            hasError = metrics.HasError
        });
    }

    public void EndSession(bool clean, string reason)
    {
        lock (syncRoot)
        {
            if (disposed || sessionEnded) return;
            if (configuration.Enabled && !suppressed)
            {
                try
                {
                    WriteUnsafe(new { timestamp = DateTimeOffset.UtcNow, sessionId, type = "event", name = "session_stopped", details = new { clean, reason } });
                }
                catch (Exception exception)
                {
                    SafeLogError("No se pudo registrar el fin de sesión del benchmark.", exception);
                }
            }
            sessionEnded = true;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            timer?.Dispose();
            timer = null;
            if (!sessionEnded && configuration.Enabled && !suppressed)
            {
                try
                {
                    WriteUnsafe(new { timestamp = DateTimeOffset.UtcNow, sessionId, type = "event", name = "session_stopped", details = new { clean = true, reason = "disposed" } });
                }
                catch (Exception exception)
                {
                    SafeLogError("No se pudo registrar el cierre del benchmark.", exception);
                }
                sessionEnded = true;
            }
            disposed = true;
        }
    }

    private void CaptureSample()
    {
        if (disposed) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            Write(new
            {
                timestamp = DateTimeOffset.UtcNow,
                sessionId,
                type = "sample",
                process = new
                {
                    pid = process.Id,
                    name = process.ProcessName,
                    privateBytes = process.PrivateMemorySize64,
                    workingSetBytes = process.WorkingSet64,
                    handles = process.HandleCount,
                    threads = process.Threads.Count,
                    totalProcessorTimeMs = process.TotalProcessorTime.TotalMilliseconds
                }
            });
        }
        catch (Exception exception)
        {
            SafeLogError("No se pudo capturar una muestra de benchmark.", exception);
        }
    }

    private void Write(object record)
    {
        lock (syncRoot)
        {
            if (disposed) return;
            WriteUnsafe(record);
        }
    }

    private void WriteUnsafe(object record)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"live-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        // Varias instancias opt-in (por ejemplo, TUI y el shell en pruebas)
        // pueden coincidir en el archivo diario. El bloqueo propio protege una
        // instancia; compartir lectura/escritura evita que una segunda sesión
        // falle solo por abrir el mismo NDJSON.
        lock (fileWriteLocks.GetOrAdd(path, static _ => new object()))
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(record));
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(directory).GetFiles("live-*.jsonl").OrderBy(file => file.LastWriteTimeUtc).ToList();
        var cutoff = DateTime.UtcNow.Date.AddDays(-configuration.RetentionDays);
        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff)) file.Delete();

        files = new DirectoryInfo(directory).GetFiles("live-*.jsonl").OrderBy(file => file.LastWriteTimeUtc).ToList();
        var limit = configuration.MaxStorageMb * 1024L * 1024L;
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= limit) break;
            total -= file.Length;
            file.Delete();
        }
    }

    private void SafeLogError(string message, Exception exception)
    {
        if (disposed) return;
        try
        {
            logger.Error(message, exception);
        }
        catch
        {
            // El diagnóstico no puede terminar el proceso si el destino de log
            // desaparece durante el apagado de una sesión.
        }
    }

    private static double? NonNegative(double? value) => value is null ? null : Math.Max(0, value.Value);
    private static long? NonNegative(long? value) => value is null ? null : Math.Max(0, value.Value);
    private static long NonNegative(long value) => Math.Max(0, value);
    private static double NonNegative(double value) => Math.Max(0, value);
}
