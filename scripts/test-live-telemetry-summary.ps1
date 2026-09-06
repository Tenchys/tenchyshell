[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$summarizer = Join-Path $PSScriptRoot "summarize-live-telemetry.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.LiveTelemetry.Tests." + [Guid]::NewGuid().ToString("N"))
$liveDirectory = Join-Path $testRoot "live"
$logDirectory = Join-Path $testRoot "logs"

function Assert-Contains([string[]]$Actual, [string]$Expected) {
    if (-not (($Actual -join "`n").Contains($Expected, [StringComparison]::Ordinal))) {
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
    $fatal = [ordered]@{
        timestamp = "2026-09-05T00:00:07.0000000+00:00"; sessionId = $session; type = "event"; name = "fatal_error"
        details = [ordered]@{ message = "synthetic" }
    } | ConvertTo-Json -Compress
    $last = [ordered]@{
        timestamp = "2026-09-05T00:00:10.0000000+00:00"; sessionId = $session; type = "sample"
        process = [ordered]@{ pid = 2; name = "TenchyShell"; privateBytes = 104MB; workingSetBytes = 79MB; handles = 12; threads = 5; totalProcessorTimeMs = 3500 }
    } | ConvertTo-Json -Compress
    [IO.File]::WriteAllLines((Join-Path $liveDirectory "live-20260904.jsonl"), @($oldRecord))
    [IO.File]::WriteAllLines((Join-Path $liveDirectory "live-20260905.jsonl"), @($first, $normalEvent, $enumeration, $fatal, "{", $last))
    [IO.File]::WriteAllLines((Join-Path $logDirectory "tenchyshell.log"), @(
        "2026-09-04T23:59:59.0000000+00:00 [ERROR] anterior",
        "2026-09-05T00:00:08.0000000+00:00 [ERROR] actual"))

    $output = @(& $summarizer -LiveDirectory $liveDirectory -LogDirectory $logDirectory)
    Assert-Contains $output "Sesión: current-session"
    Assert-Contains $output "Memoria privada (MiB): 100 → 104 (+4 MiB)"
    Assert-Contains $output "Working set (MiB): 80 → 79 (-1 MiB)"
    Assert-Contains $output "Handles: 10 → 12 (+2)"
    Assert-Contains $output "Hilos: 4 → 5 (+1)"
    Assert-Contains $output "CPU promedio: 25%"
    Assert-Contains $output "líneas inválidas ignoradas: 1"
    Assert-Contains $output "workspace_window_enumeration: 1"
    Assert-Contains $output "Errores WARN/ERROR/FATAL/CRITICAL en logs: 1"
    Assert-Contains $output "[telemetría] fatal_error"
    if (($output -join "`n").Contains("Secret title", [StringComparison]::Ordinal)) { throw "La salida filtró un título de evento normal." }

    Write-Host "Pruebas de resumen de telemetría correctas."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
