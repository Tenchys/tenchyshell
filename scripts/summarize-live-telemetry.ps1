[CmdletBinding()]
param(
    [string]$LiveDirectory = (Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\live"),

    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA "TenchyShell\logs"),

    [string]$SessionId,

    [switch]$IncludeErrorMessages
)

$ErrorActionPreference = "Stop"

function ConvertTo-Timestamp($Value) {
    if ($null -eq $Value) { return $null }
    try { return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture) }
    catch { return $null }
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 1) { return [double]$ordered[0] }
    $position = ($ordered.Count - 1) * $Percentile
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) { return [double]$ordered[$lower] }
    return [double]($ordered[$lower] + (($ordered[$upper] - $ordered[$lower]) * ($position - $lower)))
}

function Format-Number($Value, [int]$Digits = 2) {
    if ($null -eq $Value) { return "N/D" }
    return ([Math]::Round([double]$Value, $Digits)).ToString("0." + ("#" * $Digits), [Globalization.CultureInfo]::InvariantCulture).TrimEnd('.')
}

function Format-Delta($Value, [string]$Unit = "") {
    if ($null -eq $Value) { return "N/D" }
    $number = Format-Number $Value
    if ([double]$Value -gt 0) { $number = "+$number" }
    return "$number$Unit"
}

function Get-SafeMetric($Details, [string]$Name) {
    if ($null -eq $Details) { return $null }
    $property = $Details.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $null }
    try {
        $number = [double]$property.Value
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
        return [Math]::Max([double]0, $number)
    } catch {
        return $null
    }
}

function Add-Anomaly([Collections.Generic.List[object]]$Target, $Timestamp, [string]$Source, [string]$Category, [string]$Message = "") {
    $Target.Add([pscustomobject]@{
        Timestamp = $Timestamp
        Source = $Source
        Category = $Category
        Message = $Message
    })
}

if (-not (Test-Path -LiteralPath $LiveDirectory -PathType Container)) {
    throw "No existe el directorio de telemetría: $LiveDirectory"
}

$liveFiles = @(Get-ChildItem -LiteralPath $LiveDirectory -File -Filter "live-*.jsonl" | Sort-Object Name)
if ($liveFiles.Count -eq 0) { throw "No se encontraron archivos JSONL en '$LiveDirectory'." }

# Por defecto se identifica la última sesión desde el archivo más reciente y se
# recorren sólo los días contiguos que la contienen. El historial completo queda
# reservado para una sesión solicitada explícitamente.
if ([string]::IsNullOrWhiteSpace($SessionId)) {
    $latestRecord = $null
    foreach ($file in @($liveFiles | Sort-Object Name -Descending)) {
        foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $record = $line | ConvertFrom-Json }
            catch { continue }
            $timestamp = ConvertTo-Timestamp $record.timestamp
            if ($null -eq $timestamp -or [string]::IsNullOrWhiteSpace([string]$record.sessionId)) { continue }
            if ($null -eq $latestRecord -or $timestamp -gt $latestRecord.Timestamp) {
                $latestRecord = [pscustomobject]@{ Timestamp = $timestamp; SessionId = [string]$record.sessionId }
            }
        }
        if ($null -ne $latestRecord) { break }
    }
    if ($null -eq $latestRecord) { throw "No se encontraron sesiones válidas en '$LiveDirectory'." }
    $SessionId = $latestRecord.SessionId

    $filesToRead = [Collections.Generic.List[IO.FileInfo]]::new()
    $foundSession = $false
    foreach ($file in @($liveFiles | Sort-Object Name -Descending)) {
        $containsSession = $false
        foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $record = $line | ConvertFrom-Json }
            catch { continue }
            if ([string]$record.sessionId -eq $SessionId) { $containsSession = $true; break }
        }
        if ($containsSession) {
            $filesToRead.Add($file)
            $foundSession = $true
        } elseif ($foundSession) {
            break
        }
    }
} else {
    $filesToRead = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($file in $liveFiles) { $filesToRead.Add($file) }
}

$invalidJsonLines = 0
$samples = [Collections.Generic.List[object]]::new()
$eventCounts = @{}
$enumerationDurations = [Collections.Generic.List[double]]::new()
$anomalies = [Collections.Generic.List[object]]::new()
$fileLoggerMetrics = $null
$notificationCenterMetrics = $null
$notificationSurfaceMetrics = $null
$sessionFirst = $null
$sessionLast = $null

# Conservar sólo las muestras de la sesión y agregados de eventos.
foreach ($file in $filesToRead) {
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $record = $line | ConvertFrom-Json }
        catch { $invalidJsonLines++; continue }
        $timestamp = ConvertTo-Timestamp $record.timestamp
        $recordSessionId = [string]$record.sessionId
        if ($null -eq $timestamp -or [string]::IsNullOrWhiteSpace($recordSessionId)) { $invalidJsonLines++; continue }
        if ($recordSessionId -ne $SessionId) { continue }
        if ($null -eq $sessionFirst -or $timestamp -lt $sessionFirst) { $sessionFirst = $timestamp }
        if ($null -eq $sessionLast -or $timestamp -gt $sessionLast) { $sessionLast = $timestamp }
        if ([string]$record.type -eq "sample" -and $null -ne $record.process) {
            $samples.Add([pscustomobject]@{
                Timestamp = $timestamp
                PrivateBytes = [double]$record.process.privateBytes
                WorkingSetBytes = [double]$record.process.workingSetBytes
                Handles = [double]$record.process.handles
                Threads = [double]$record.process.threads
                TotalProcessorTimeMs = [double]$record.process.totalProcessorTimeMs
            })
            continue
        }

        if ([string]$record.type -ne "event") { continue }
        $name = [string]$record.name
        if ([string]::IsNullOrWhiteSpace($name)) { $name = "(sin nombre)" }
        if ($eventCounts.ContainsKey($name)) { $eventCounts[$name]++ } else { $eventCounts[$name] = 1 }

        if ($name -eq "workspace_window_enumeration" -and $null -ne $record.details.durationMs) {
            $enumerationDurations.Add([double]$record.details.durationMs)
        }

        if ($name -eq "file_logger_metrics") {
            # El evento es un agregado de sesión. Si una fuente lo publica más
            # de una vez, el último registro representa el estado más reciente.
            if ($null -eq $fileLoggerMetrics -or $timestamp -ge $fileLoggerMetrics.Timestamp) {
                $fileLoggerMetrics = [pscustomobject]@{
                    Timestamp = $timestamp
                    LogCalls = Get-SafeMetric $record.details "logCalls"
                    WriteFailures = Get-SafeMetric $record.details "writeFailures"
                    MaintenanceCalls = Get-SafeMetric $record.details "maintenanceCalls"
                    ArchiveScans = Get-SafeMetric $record.details "archiveScans"
                    ArchiveFilesSeen = Get-SafeMetric $record.details "archiveFilesSeen"
                    ArchiveFilesDeleted = Get-SafeMetric $record.details "archiveFilesDeleted"
                    RotationsByDay = Get-SafeMetric $record.details "rotationsByDay"
                    RotationsBySize = Get-SafeMetric $record.details "rotationsBySize"
                    ContentionCount = Get-SafeMetric $record.details "contentionCount"
                    ContentionWaitMilliseconds = Get-SafeMetric $record.details "contentionWaitMilliseconds"
                    WriteDurationP95Milliseconds = Get-SafeMetric $record.details "writeDurationP95Milliseconds"
                    MaintenanceDurationP95Milliseconds = Get-SafeMetric $record.details "maintenanceDurationP95Milliseconds"
                    BytesWritten = Get-SafeMetric $record.details "bytesWritten"
                    ArchiveCountAtEnd = Get-SafeMetric $record.details "archiveCountAtEnd"
                }
            }
        }

        if ($name -eq "notification_center_metrics") {
            if ($null -eq $notificationCenterMetrics -or $timestamp -ge $notificationCenterMetrics.Timestamp) {
                $metricNames = @(
                    "addCount", "addTotalMilliseconds", "addAllocatedBytes", "addMaximumMilliseconds", "addMaximumAllocatedBytes", "addDurationP95Milliseconds",
                    "getActiveCount", "getActiveTotalMilliseconds", "getActiveAllocatedBytes", "getActiveMaximumMilliseconds", "getActiveMaximumAllocatedBytes", "getActiveDurationP95Milliseconds",
                    "removeCount", "removeTotalMilliseconds", "removeAllocatedBytes", "removeMaximumMilliseconds", "removeMaximumAllocatedBytes", "removeDurationP95Milliseconds",
                    "trimCalls", "trimmedItems"
                )
                $notificationCenterMetrics = [ordered]@{ Timestamp = $timestamp }
                foreach ($metricName in $metricNames) { $notificationCenterMetrics[$metricName] = Get-SafeMetric $record.details $metricName }
            }
        }

        if ($name -eq "notification_surface_metrics") {
            if ($null -eq $notificationSurfaceMetrics -or $timestamp -ge $notificationSurfaceMetrics.Timestamp) {
                $metricNames = @(
                    "repaints", "iconDecodeAttempts", "totalIconDecodeDurationMilliseconds", "maximumIconDecodeDurationMilliseconds",
                    "totalPaintDurationMilliseconds", "maximumPaintDurationMilliseconds", "p95PaintDurationMilliseconds",
                    "iconBytesPresentedToDecoder", "iconBytesWritten"
                )
                $notificationSurfaceMetrics = [ordered]@{ Timestamp = $timestamp }
                foreach ($metricName in $metricNames) { $notificationSurfaceMetrics[$metricName] = Get-SafeMetric $record.details $metricName }
            }
        }

        $isAnomaly = $name -in @("fatal_error", "explorer_recovery_blocked", "explorer_recovery_failed", "window_focus_attach_failed")
        if ($name -eq "window_focus_result" -and $null -ne $record.details.focused -and -not [bool]$record.details.focused) { $isAnomaly = $true }
        if ($name -eq "window_switcher_confirmation") {
            $failure = [string]$record.details.failure
            if ((-not [string]::IsNullOrWhiteSpace($failure) -and $failure -ne "None") -or ($null -ne $record.details.focused -and -not [bool]$record.details.focused)) {
                $isAnomaly = $true
            }
        }
        if ($isAnomaly) { Add-Anomaly $anomalies $timestamp "telemetría" $name }
    }
}

if ($null -eq $sessionFirst) { throw "No se encontró la sesión '$SessionId' en '$LiveDirectory'." }
$sessionBounds = [pscustomobject]@{ First = $sessionFirst; Last = $sessionLast }

$samples = @($samples | Sort-Object Timestamp)
$sampleIntervals = [Collections.Generic.List[double]]::new()
for ($index = 1; $index -lt $samples.Count; $index++) {
    $sampleIntervals.Add(($samples[$index].Timestamp - $samples[$index - 1].Timestamp).TotalSeconds)
}

$logErrorCount = 0
if (Test-Path -LiteralPath $LogDirectory -PathType Container) {
    $logPattern = '^(?<timestamp>\d{4}-\d{2}-\d{2}T[^\s]+) \[(?<level>WARN|ERROR|FATAL|CRITICAL)\] (?<message>.*)$'
    foreach ($logFile in @(Get-ChildItem -LiteralPath $LogDirectory -File -Filter "*.log")) {
        foreach ($line in [IO.File]::ReadLines($logFile.FullName)) {
            $match = [regex]::Match($line, $logPattern)
            if (-not $match.Success) { continue }
            $timestamp = ConvertTo-Timestamp $match.Groups["timestamp"].Value
            if ($null -eq $timestamp -or $timestamp -lt $sessionBounds.First -or $timestamp -gt $sessionBounds.Last) { continue }
            $logErrorCount++
            $message = if ($IncludeErrorMessages) { $match.Groups["message"].Value.Trim() } else { "" }
            Add-Anomaly $anomalies $timestamp "log" $match.Groups["level"].Value $message
        }
    }
}

Write-Output "TenchyShell — resumen de telemetría local"
Write-Output "Sesión: $SessionId"
Write-Output "Periodo: $($sessionBounds.First.ToString('O')) → $($sessionBounds.Last.ToString('O')) ($([Math]::Round(($sessionBounds.Last - $sessionBounds.First).TotalMinutes, 1)) min)"
Write-Output "JSONL: $($filesToRead.Count) archivo(s); líneas inválidas ignoradas: $invalidJsonLines"
Write-Output ""
Write-Output "Recursos"

if ($samples.Count -eq 0) {
    Write-Output "Sin muestras de proceso en esta sesión."
} else {
    $first = $samples[0]
    $last = $samples[-1]
    $elapsedMs = ($last.Timestamp - $first.Timestamp).TotalMilliseconds
    $cpuDeltaMs = $last.TotalProcessorTimeMs - $first.TotalProcessorTimeMs
    $cpuPercent = if ($elapsedMs -gt 0) { ($cpuDeltaMs / $elapsedMs) * 100.0 } else { $null }
    Write-Output "Muestras: $($samples.Count); primera: $($first.Timestamp.ToString('O')); última: $($last.Timestamp.ToString('O'))"
    Write-Output ("Memoria privada (MiB): {0} → {1} ({2})" -f (Format-Number ($first.PrivateBytes / 1MB)), (Format-Number ($last.PrivateBytes / 1MB)), (Format-Delta (($last.PrivateBytes - $first.PrivateBytes) / 1MB) " MiB"))
    Write-Output ("Working set (MiB): {0} → {1} ({2})" -f (Format-Number ($first.WorkingSetBytes / 1MB)), (Format-Number ($last.WorkingSetBytes / 1MB)), (Format-Delta (($last.WorkingSetBytes - $first.WorkingSetBytes) / 1MB) " MiB"))
    Write-Output ("Handles: {0} → {1} ({2})" -f (Format-Number $first.Handles 0), (Format-Number $last.Handles 0), (Format-Delta ($last.Handles - $first.Handles)))
    Write-Output ("Hilos: {0} → {1} ({2})" -f (Format-Number $first.Threads 0), (Format-Number $last.Threads 0), (Format-Delta ($last.Threads - $first.Threads)))
    Write-Output ("CPU acumulada: {0} ms ({1}); CPU promedio: {2}%" -f (Format-Number $cpuDeltaMs), (Format-Delta $cpuDeltaMs " ms"), (Format-Number $cpuPercent))
    if ($sampleIntervals.Count -gt 0) {
        $gapsOverMinute = @($sampleIntervals | Where-Object { $_ -gt 60 }).Count
        Write-Output ("Cadencia: mediana {0} s; máximo {1} s; huecos >60 s: {2}" -f (Format-Number (Get-Percentile $sampleIntervals.ToArray() 0.5)), (Format-Number (($sampleIntervals | Measure-Object -Maximum).Maximum)), $gapsOverMinute)
    }
}

Write-Output ""
Write-Output "Actividad agregada"
if ($eventCounts.Count -eq 0) {
    Write-Output "Sin eventos en esta sesión."
} else {
    foreach ($event in @($eventCounts.GetEnumerator() | Sort-Object -Property @{ Expression = { $_.Value }; Descending = $true }, @{ Expression = { $_.Key }; Descending = $false })) {
        Write-Output ("{0}: {1}" -f $event.Key, $event.Value)
    }
}
if ($enumerationDurations.Count -gt 0) {
    $durations = $enumerationDurations.ToArray()
    Write-Output ("Enumeración de ventanas (ms): muestras {0}; mediana {1}; P95 {2}; máximo {3}" -f $durations.Count, (Format-Number (Get-Percentile $durations 0.5)), (Format-Number (Get-Percentile $durations 0.95)), (Format-Number (($durations | Measure-Object -Maximum).Maximum)))
}

Write-Output ""
Write-Output "Centro de notificaciones"
if ($null -eq $notificationCenterMetrics) {
    Write-Output "Sin métricas notification_center_metrics en esta sesión."
} else {
    foreach ($metricName in @("add", "getActive", "remove")) {
        $prefix = $metricName
        $label = switch ($metricName) { "add" { "Añadir" } "getActive" { "Consultar activas" } "remove" { "Eliminar" } }
        Write-Output ("{0}: llamadas {1}; total {2} ms; asignados {3} bytes; máximo {4} ms / {5} bytes; P95 {6} ms" -f $label,
            (Format-Number $notificationCenterMetrics[($prefix + "Count")] 0),
            (Format-Number $notificationCenterMetrics[($prefix + "TotalMilliseconds")]),
            (Format-Number $notificationCenterMetrics[($prefix + "AllocatedBytes")] 0),
            (Format-Number $notificationCenterMetrics[($prefix + "MaximumMilliseconds")]),
            (Format-Number $notificationCenterMetrics[($prefix + "MaximumAllocatedBytes")] 0),
            (Format-Number $notificationCenterMetrics[($prefix + "DurationP95Milliseconds")]))
    }
    Write-Output ("Recorte: llamadas {0}; elementos descartados {1}" -f (Format-Number $notificationCenterMetrics.trimCalls 0), (Format-Number $notificationCenterMetrics.trimmedItems 0))
}

Write-Output ""
Write-Output "Superficie de notificaciones"
if ($null -eq $notificationSurfaceMetrics) {
    Write-Output "Sin métricas notification_surface_metrics en esta sesión."
} else {
    foreach ($metricName in @("repaints", "iconDecodeAttempts", "totalIconDecodeDurationMilliseconds", "maximumIconDecodeDurationMilliseconds", "totalPaintDurationMilliseconds", "maximumPaintDurationMilliseconds", "p95PaintDurationMilliseconds", "iconBytesPresentedToDecoder", "iconBytesWritten")) {
        Write-Output ("{0}: {1}" -f $metricName, (Format-Number $notificationSurfaceMetrics[$metricName] 2))
    }
}

Write-Output ""
Write-Output "Logger de archivos"
if ($null -eq $fileLoggerMetrics) {
    Write-Output "Sin métricas file_logger_metrics en esta sesión."
} else {
    $maintenancePerLogCall = if ($fileLoggerMetrics.LogCalls -gt 0 -and $null -ne $fileLoggerMetrics.MaintenanceCalls) {
        $fileLoggerMetrics.MaintenanceCalls / $fileLoggerMetrics.LogCalls
    } else { $null }
    Write-Output ("Llamadas de log: {0}; fallos de escritura: {1}; bytes escritos: {2}" -f (Format-Number $fileLoggerMetrics.LogCalls 0), (Format-Number $fileLoggerMetrics.WriteFailures 0), (Format-Number $fileLoggerMetrics.BytesWritten 0))
    Write-Output ("Mantenimientos: {0}; por llamada: {1}; enumeraciones de archivados: {2}" -f (Format-Number $fileLoggerMetrics.MaintenanceCalls 0), (Format-Number $maintenancePerLogCall 4), (Format-Number $fileLoggerMetrics.ArchiveScans 0))
    Write-Output ("Archivos archivados vistos: {0}; eliminados: {1}; al final: {2}" -f (Format-Number $fileLoggerMetrics.ArchiveFilesSeen 0), (Format-Number $fileLoggerMetrics.ArchiveFilesDeleted 0), (Format-Number $fileLoggerMetrics.ArchiveCountAtEnd 0))
    Write-Output ("Rotaciones por día: {0}; por tamaño: {1}" -f (Format-Number $fileLoggerMetrics.RotationsByDay 0), (Format-Number $fileLoggerMetrics.RotationsBySize 0))
    Write-Output ("Contención: {0}; espera acumulada: {1} ms" -f (Format-Number $fileLoggerMetrics.ContentionCount 0), (Format-Number $fileLoggerMetrics.ContentionWaitMilliseconds 2))
    Write-Output ("P95 escritura: {0} ms; P95 mantenimiento: {1} ms" -f (Format-Number $fileLoggerMetrics.WriteDurationP95Milliseconds 2), (Format-Number $fileLoggerMetrics.MaintenanceDurationP95Milliseconds 2))
}

Write-Output ""
Write-Output "Anomalías"
Write-Output "Errores WARN/ERROR/FATAL/CRITICAL en logs: $logErrorCount"
if ($anomalies.Count -eq 0) {
    Write-Output "No se detectaron anomalías de telemetría ni errores de logs en el periodo."
} else {
    foreach ($anomaly in @($anomalies | Sort-Object Timestamp -Descending | Select-Object -First 10)) {
        $suffix = if ($IncludeErrorMessages -and -not [string]::IsNullOrWhiteSpace($anomaly.Message)) { ": $($anomaly.Message)" } else { "" }
        Write-Output ("{0} [{1}] {2}{3}" -f $anomaly.Timestamp.ToString('O'), $anomaly.Source, $anomaly.Category, $suffix)
    }
    if ($anomalies.Count -gt 10) { Write-Output "Se muestran 10 de $($anomalies.Count) anomalías." }
}
