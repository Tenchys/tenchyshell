using System.Diagnostics;
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
    private readonly BenchmarkConfiguration configuration;
    private readonly ILogger logger;
    private readonly string directory;
    private readonly object syncRoot = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private Timer? timer;
    private bool disposed;

    public LiveBenchmarkRecorder(BenchmarkConfiguration configuration, ILogger logger, string? rootDirectory = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TenchyShell", "benchmarks", "live");
    }

    public string DirectoryPath => directory;

    public bool IsEnabled => configuration.Enabled && !disposed;

    public void Start()
    {
        if (!configuration.Enabled || disposed) return;
        try
        {
            Directory.CreateDirectory(directory);
            Prune();
            Record("session_started", new { profile = configuration.CaptureProfile });
            timer = new Timer(_ => CaptureSample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(configuration.SampleIntervalSeconds));
        }
        catch (Exception exception)
        {
            SafeLogError("No se pudo iniciar el benchmark de sesión; TenchyShell continuará activo.", exception);
        }
    }

    public void Record(string eventName, object? details = null)
    {
        if (!configuration.Enabled || disposed) return;
        try
        {
            Write(new { timestamp = DateTimeOffset.UtcNow, sessionId, type = "event", name = eventName, details });
        }
        catch (Exception exception)
        {
            SafeLogError($"No se pudo registrar el evento de benchmark '{eventName}'.", exception);
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed) return;
            timer?.Dispose();
            timer = null;
            WriteUnsafe(new { timestamp = DateTimeOffset.UtcNow, sessionId, type = "event", name = "session_stopped", details = (object?)null });
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
        File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
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
}
