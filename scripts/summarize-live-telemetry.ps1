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
