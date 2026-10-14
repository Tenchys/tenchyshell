[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$summarizer = Join-Path $PSScriptRoot "summarize-live-telemetry.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.LiveTelemetry.Tests." + [Guid]::NewGuid().ToString("N"))
$liveDirectory = Join-Path $testRoot "live"
$logDirectory = Join-Path $testRoot "logs"

function Assert-Contains([string[]]$Actual, [string]$Expected) {
    if (-not (($Actual -join "`n").Contains($Expected))) {
        throw "No se encontró '$Expected' en la salida: $($Actual -join ' | ')"
    }
}

try {
    New-Item -ItemType Directory -Path $liveDirectory, $logDirectory | Out-Null
    $oldSession = "old-session"
    $session = "current-session"
    $oldRecord = [ordered]@{
        timestamp = "2026-09-04T23:59:00.0000000+00:00"; sessionId = $oldSession; type = "sample"
        process = [ordered]@{ pid = 1; name = "TenchyShell"; privateBytes = 1MB; workingSetBytes = 1MB; handles = 1; threads = 1; totalProcessorTimeMs = 0 }
    } | ConvertTo-Json -Compress
    $first = [ordered]@{
        timestamp = "2026-09-05T00:00:00.0000000+00:00"; sessionId = $session; type = "sample"
        process = [ordered]@{ pid = 2; name = "TenchyShell"; privateBytes = 100MB; workingSetBytes = 80MB; handles = 10; threads = 4; totalProcessorTimeMs = 1000 }
    } | ConvertTo-Json -Compress
    $normalEvent = [ordered]@{
        timestamp = "2026-09-05T00:00:05.0000000+00:00"; sessionId = $session; type = "event"; name = "application_requested"
        details = [ordered]@{ title = "Secret title"; command = "secret-command" }
    } | ConvertTo-Json -Compress
    $enumeration = [ordered]@{
        timestamp = "2026-09-05T00:00:06.0000000+00:00"; sessionId = $session; type = "event"; name = "workspace_window_enumeration"
        details = [ordered]@{ durationMs = 8.0; includedCount = 2 }
    } | ConvertTo-Json -Compress
    $fileLoggerMetrics = [ordered]@{
        timestamp = "2026-09-05T00:00:06.5000000+00:00"; sessionId = $session; type = "event"; name = "file_logger_metrics"
        details = [ordered]@{
            logCalls = 120; writeFailures = 0; maintenanceCalls = 4; archiveScans = 4
            archiveFilesSeen = 18; archiveFilesDeleted = 3; rotationsByDay = 1; rotationsBySize = 0
            contentionCount = 2; contentionWaitMilliseconds = 0.8; writeDurationP95Milliseconds = 0.35
            maintenanceDurationP95Milliseconds = 1.7; bytesWritten = 145820; archiveCountAtEnd = 15
            message = "must not be displayed"; path = "C:\\secret"
        }
    } | ConvertTo-Json -Compress
    $notificationCenterMetrics = [ordered]@{
        timestamp = "2026-09-05T00:00:06.7500000+00:00"; sessionId = $session; type = "event"; name = "notification_center_metrics"
        details = [ordered]@{
            addCount = 10; addTotalMilliseconds = 12.5; addAllocatedBytes = 1000; addMaximumMilliseconds = 3.5; addMaximumAllocatedBytes = 500; addDurationP95Milliseconds = 2.5
            getActiveCount = 20; getActiveTotalMilliseconds = 8; getActiveAllocatedBytes = 2000; getActiveMaximumMilliseconds = 1.5; getActiveMaximumAllocatedBytes = 600; getActiveDurationP95Milliseconds = 0.8
            removeCount = 4; removeTotalMilliseconds = 6; removeAllocatedBytes = 300; removeMaximumMilliseconds = 2; removeMaximumAllocatedBytes = 100; removeDurationP95Milliseconds = 1.7
            trimCalls = 2; trimmedItems = 3
            title = "NOTIFICATION_SECRET_TITLE"; path = "NOTIFICATION_SECRET_PATH"; id = "NOTIFICATION_SECRET_ID"; body = "NOTIFICATION_SECRET_BODY"
        }
    } | ConvertTo-Json -Compress
    $notificationSurfaceMetrics = [ordered]@{
        timestamp = "2026-09-05T00:00:06.8000000+00:00"; sessionId = $session; type = "event"; name = "notification_surface_metrics"
        details = [ordered]@{
            repaints = 11; iconDecodeAttempts = 5; totalIconDecodeDurationMilliseconds = 4.5; maximumIconDecodeDurationMilliseconds = 2.5
            totalPaintDurationMilliseconds = 18; maximumPaintDurationMilliseconds = 4; p95PaintDurationMilliseconds = 3.5
            iconBytesPresentedToDecoder = 4096; iconBytesWritten = 2048
            title = "SURFACE_SECRET_TITLE"; path = "SURFACE_SECRET_PATH"; id = "SURFACE_SECRET_ID"; body = "SURFACE_SECRET_BODY"
        }
    } | ConvertTo-Json -Compress
    $fatal = [ordered]@{
        timestamp = "2026-09-05T00:00:07.0000000+00:00"; sessionId = $session; type = "event"; name = "fatal_error"
        details = [ordered]@{ message = "synthetic" }
    } | ConvertTo-Json -Compress
    $last = [ordered]@{
        timestamp = "2026-09-05T00:00:10.0000000+00:00"; sessionId = $session; type = "sample"
        process = [ordered]@{ pid = 2; name = "TenchyShell"; privateBytes = 104MB; workingSetBytes = 79MB; handles = 12; threads = 5; totalProcessorTimeMs = 3500 }
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllLines((Join-Path $liveDirectory "live-20260904.jsonl"), @($oldRecord))
    [IO.File]::WriteAllLines((Join-Path $liveDirectory "live-20260905.jsonl"), @($first, $normalEvent, $enumeration, $fileLoggerMetrics, $notificationCenterMetrics, $notificationSurfaceMetrics, $fatal, "{", $last))
    [IO.File]::WriteAllLines((Join-Path $logDirectory "tenchyshell.log"), @(
        "2026-09-04T23:59:59.0000000+00:00 [ERROR] anterior",
        "2026-09-05T00:00:08.0000000+00:00 [ERROR] actual"))

    $output = @(& $summarizer -LiveDirectory $liveDirectory -LogDirectory $logDirectory)
    Assert-Contains $output "current-session"
    Assert-Contains $output "Memoria privada (MiB): 100"
    Assert-Contains $output "Working set (MiB): 80"
    Assert-Contains $output "Handles: 10"
    Assert-Contains $output "Hilos: 4"
    Assert-Contains $output "CPU promedio: 25%"
    Assert-Contains $output "JSONL: 1 archivo(s)"
    Assert-Contains $output "workspace_window_enumeration: 1"
    Assert-Contains $output "Llamadas de log: 120; fallos de escritura: 0; bytes escritos: 145820"
    Assert-Contains $output "Mantenimientos: 4; por llamada: 0.0333; enumeraciones de archivados: 4"
    Assert-Contains $output "Archivos archivados vistos: 18; eliminados: 3; al final: 15"
    Assert-Contains $output "P95 escritura: 0.35 ms; P95 mantenimiento: 1.7 ms"
    Assert-Contains $output "total 12.5 ms; asignados 1000 bytes"
    Assert-Contains $output "llamadas 20; total 8 ms; asignados 2000 bytes"
    Assert-Contains $output "llamadas 4; total 6 ms; asignados 300 bytes"
    Assert-Contains $output "Recorte: llamadas 2; elementos descartados 3"
    Assert-Contains $output "repaints: 11"
    Assert-Contains $output "iconDecodeAttempts: 5"
    Assert-Contains $output "totalIconDecodeDurationMilliseconds: 4.5"
    Assert-Contains $output "maximumIconDecodeDurationMilliseconds: 2.5"
    Assert-Contains $output "totalPaintDurationMilliseconds: 18"
    Assert-Contains $output "maximumPaintDurationMilliseconds: 4"
    Assert-Contains $output "p95PaintDurationMilliseconds: 3.5"
    Assert-Contains $output "iconBytesPresentedToDecoder: 4096"
    Assert-Contains $output "iconBytesWritten: 2048"
    Assert-Contains $output "Errores WARN/ERROR/FATAL/CRITICAL en logs: 1"
    Assert-Contains $output "fatal_error"
    $outputText = $output -join "`n"
    if ($outputText.Contains("Secret title")) { throw "La salida filtró un título de evento normal." }
    if ($outputText.Contains("must not be displayed") -or
        $outputText.Contains("C:\\secret")) { throw "La salida filtró campos no permitidos del logger." }

    foreach ($secret in @("NOTIFICATION_SECRET_TITLE", "NOTIFICATION_SECRET_PATH", "NOTIFICATION_SECRET_ID", "NOTIFICATION_SECRET_BODY", "SURFACE_SECRET_TITLE", "SURFACE_SECRET_PATH", "SURFACE_SECRET_ID", "SURFACE_SECRET_BODY")) {
        if ($outputText.Contains($secret)) { throw "La salida filtró un detalle privado de notificaciones: $secret" }
    }

    $historicalOutput = @(& $summarizer -LiveDirectory $liveDirectory -LogDirectory $logDirectory -SessionId $oldSession)
    Assert-Contains $historicalOutput "old-session"
    Assert-Contains $historicalOutput "file_logger_metrics"

    Write-Host "Pruebas de resumen de telemetría correctas."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
