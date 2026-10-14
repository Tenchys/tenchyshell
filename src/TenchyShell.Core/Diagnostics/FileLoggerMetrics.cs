namespace TenchyShell.Core.Diagnostics;

/// <summary>
/// Agregado seguro del coste del logger de archivos. No contiene rutas,
/// mensajes, comandos, argumentos ni contenido de sesión.
/// </summary>
public sealed record FileLoggerMetrics(
    long LogCalls,
    long WriteFailures,
    long MaintenanceCalls,
    long ArchiveScans,
    long ArchiveFilesSeen,
    long ArchiveFilesDeleted,
    long RotationsByDay,
    long RotationsBySize,
    long ContentionCount,
    double ContentionWaitMilliseconds,
    double? WriteDurationP95Milliseconds,
    double? MaintenanceDurationP95Milliseconds,
    long BytesWritten,
    long ArchiveCountAtEnd);
