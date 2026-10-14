param(
    [string]$BatchId = "",
    [switch]$SmokeTest,
    [ValidateRange(-1, 1800)]
    [int]$StabilizationSeconds = -1,
    [string]$RequiredUser = "serod",
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$measureScript = Join-Path $PSScriptRoot "measure-performance.ps1"
$publishDirectory = Join-Path $repoRoot "publish\TenchyShell\Release\win-x64"
$shellExecutable = Join-Path $publishDirectory "TenchyShell.exe"
$withoutExplorerConfiguration = Join-Path $publishDirectory "TenchyShell.without-explorer.example.toml"
$releaseManifestPath = Join-Path $publishDirectory "benchmark-release.json"
$benchmarkRoot = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\diagnostic"
$liveBenchmarkDirectory = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\live"
$logPath = Join-Path $env:LOCALAPPDATA "TenchyShell\logs\tenchyshell.log"
$benchmarkSessionId = (Get-Process -Id $PID).SessionId
$toolNames = @("wezterm", "wezterm-gui", "yazi")
$voluntaryProcessNames = @("brave", "chrome", "firefox", "msedge", "opera", "dropbox", "onedrive")
$matrix = @(
    [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "Idle" }
)

if (-not $SmokeTest -and [string]::IsNullOrWhiteSpace($BatchId)) { throw "El benchmark oficial requiere -BatchId." }
if (-not [string]::IsNullOrWhiteSpace($BatchId) -and $BatchId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw "BatchId solo admite letras ASCII, números, punto, guion y guion bajo (máximo 64 caracteres)."
}
if ($StabilizationSeconds -lt 0) { $StabilizationSeconds = if ($SmokeTest) { 0 } else { 300 } }

if (-not ("TenchyShellPerformanceOrchestratorInputV2" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class TenchyShellPerformanceOrchestratorInputV2
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr window, bool altTab);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public static bool TrySendQuit(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        const int Restore = 9;
        uint windowProcessId;
        var targetThreadId = GetWindowThreadProcessId(window, out windowProcessId);
        var currentThreadId = GetCurrentThreadId();
        var attached = targetThreadId != 0 && targetThreadId != currentThreadId &&
            AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            ShowWindow(window, Restore);
            BringWindowToTop(window);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (attempt == 0) SwitchToThisWindow(window, true);
                SetForegroundWindow(window);
                SetActiveWindow(window);
                BringWindowToTop(window);
                if (GetForegroundWindow() == window) break;
                Thread.Sleep(50);
            }
            if (GetForegroundWindow() != window) return false;
            Thread.Sleep(100);
            if (GetForegroundWindow() != window) return false;
        const byte Q = 0x51;
        const uint KeyUp = 0x0002;
        keybd_event(Q, 0, 0, UIntPtr.Zero);
        keybd_event(Q, 0, KeyUp, UIntPtr.Zero);
        return true;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    public static bool IsShellSurfacePresent()
    {
        return FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
    }
}
'@
}

function Get-ToolProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq $benchmarkSessionId -and $toolNames -contains $_.ProcessName.ToLowerInvariant()
    })
}

function Get-SessionProcessIds {
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { [int]$_.SessionId -eq $benchmarkSessionId } |
        ForEach-Object { [int]$_.ProcessId })
}

function Get-SessionProcessSnapshot {
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { [int]$_.SessionId -eq $benchmarkSessionId })
}

function Test-IdleWorkloadCommand([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    return $CommandLine -match '--class\s+["'']?TenchyShellBenchmarkIdle'
}

function Get-IdleWorkloadRoots([int[]]$ProtectedIds) {
    $protected = [Collections.Generic.HashSet[int]]::new()
    foreach ($id in @($ProtectedIds)) { [void]$protected.Add([int]$id) }
    return @(Get-SessionProcessSnapshot | Where-Object {
        -not $protected.Contains([int]$_.ProcessId) -and
        @("wezterm", "wezterm-gui") -contains ([IO.Path]::GetFileNameWithoutExtension([string]$_.Name).ToLowerInvariant()) -and
        (Test-IdleWorkloadCommand ([string]$_.CommandLine))
    })
}

function Get-ProcessTreeIds([int[]]$RootIds, [int[]]$ProtectedIds) {
    $protected = [Collections.Generic.HashSet[int]]::new()
    foreach ($id in @($ProtectedIds)) { [void]$protected.Add([int]$id) }
    $ids = [Collections.Generic.HashSet[int]]::new()
    foreach ($id in @($RootIds)) {
        if ([int]$id -gt 0 -and -not $protected.Contains([int]$id)) { [void]$ids.Add([int]$id) }
    }

    $snapshot = @(Get-SessionProcessSnapshot)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $snapshot) {
            $processId = [int]$process.ProcessId
            $parentId = [int]$process.ParentProcessId
            if (-not $protected.Contains($processId) -and $ids.Contains($parentId) -and $ids.Add($processId)) {
                $changed = $true
            }
        }
    }
    return @($ids)
}

function Get-RemainingProcessDetails([int[]]$ProcessIds) {
    $ids = [Collections.Generic.HashSet[int]]::new()
    foreach ($id in @($ProcessIds)) { [void]$ids.Add([int]$id) }
    return @(Get-SessionProcessSnapshot | Where-Object { $ids.Contains([int]$_.ProcessId) } |
        Sort-Object ProcessId |
        ForEach-Object {
            $command = [string]$_.CommandLine
            if ($command.Length -gt 180) { $command = $command.Substring(0, 180) + "..." }
            "$($_.Name)[$($_.ProcessId)] parent[$($_.ParentProcessId)] command=[$command]"
        })
}

function Get-TenchyShellProcesses {
    return @(Get-Process -Name TenchyShell -ErrorAction SilentlyContinue | Where-Object SessionId -eq $benchmarkSessionId)
}

function Test-DesktopShellSurfaceAbsent {
    return -not [TenchyShellPerformanceOrchestratorInputV2]::IsShellSurfacePresent()
}

function Wait-Condition([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$FailureMessage) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Wait-TenchyShellReady([Diagnostics.Process]$Process, [int]$TimeoutSeconds = 35) {
    Wait-Condition {
        if ($Process.HasExited) { throw "TenchyShell terminó antes del estado sin superficie de shell (código $($Process.ExitCode))." }
        $owned = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
        $null -ne $owned -and $owned.SessionId -eq $benchmarkSessionId -and
            $owned.ProcessName -eq "TenchyShell" -and (Test-DesktopShellSurfaceAbsent)
    } $TimeoutSeconds "TenchyShell no alcanzó el estado estable sin superficie de shell."
}

function Get-CaptureSettings([string]$Phase) {
    if ($SmokeTest) {
        return [ordered]@{ Repetitions = 1; SamplesPerRepetition = 7; IntervalMilliseconds = 1000; WarmupSeconds = 0; InterRepetitionSeconds = 0; WorkflowLaunchSecond = 1; WorkflowCloseSecond = 3; WorkflowVerifyClosedSecond = 5; StressActionSeconds = @(1, 2, 3, 4, 5) }
    }
    return [ordered]@{ Repetitions = 5; SamplesPerRepetition = 30; IntervalMilliseconds = 1000; WarmupSeconds = 10; InterRepetitionSeconds = 15; WorkflowLaunchSecond = 5; WorkflowCloseSecond = 20; WorkflowVerifyClosedSecond = 25; StressActionSeconds = @(5, 10, 15, 20, 25) }
}

function Get-CollectorDurationSeconds($Settings) {
    $measurement = [double]$Settings.Repetitions * [double]$Settings.SamplesPerRepetition * [double]$Settings.IntervalMilliseconds / 1000
    $rests = [Math]::Max(0, [int]$Settings.Repetitions - 1) * [int]$Settings.InterRepetitionSeconds
    return [int][Math]::Ceiling([int]$Settings.WarmupSeconds + $measurement + $rests)
}

function Read-ReleaseManifest {
    foreach ($path in @($shellExecutable, $withoutExplorerConfiguration, $releaseManifestPath, $measureScript)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Falta el artefacto requerido: $path" }
    }
    $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or $manifest.configuration -ne "Release" -or $manifest.runtime -ne "win-x64") { throw "benchmark-release.json no describe una publicación Release win-x64 compatible." }
    return $manifest
}

function Start-IdleTools {
    param([int[]]$ProtectedIds = @())
    if (@($ProtectedIds).Count -eq 0) { $ProtectedIds = @(Get-SessionProcessIds) }
    Start-Process -FilePath "wezterm-gui.exe" -ArgumentList @("start", "--always-new-process", "--class", "TenchyShellBenchmarkIdle", "--", "yazi.exe") | Out-Null
    Wait-Condition {
        $tools = @(Get-ToolProcesses | Where-Object { $_.Id -notin $ProtectedIds })
        @($tools | Where-Object ProcessName -eq "yazi").Count -ge 1 -and @($tools | Where-Object { $_.ProcessName -in @("wezterm", "wezterm-gui") }).Count -ge 1
    } 15 "No aparecieron WezTerm y Yazi dentro del tiempo esperado."
    return $ProtectedIds
}

function Close-OwnedToolsNormally([int[]]$ProtectedIds, [hashtable]$WorkloadState) {
    $roots = @(Get-IdleWorkloadRoots $ProtectedIds)
    if ($roots.Count -eq 0) {
        # La llamada puede producirse por segunda vez, después de que la raíz
        # haya salido. Solo se conserva el árbol capturado en la primera pasada;
        # nunca se infiere propiedad por el nombre de un proceso.
        $knownIds = @($WorkloadState.ProcessIds)
        if ($knownIds.Count -eq 0) { return }
        $targetState = $WorkloadState
        $targetState.ProcessIds = @(Get-ProcessTreeIds $targetState.ProcessIds $ProtectedIds)
        $remaining = @(Get-RemainingProcessDetails $targetState.ProcessIds)
        if ($remaining.Count -eq 0) { return }
        $details = $remaining -join "; "
        throw "Se encontraron procesos del workload Idle sin raíz WezTerm; no se forzó ningún proceso. Restantes: $details"
    }
    if ($roots.Count -ne 1) { throw "Se esperaba una ventana propia de WezTerm y se encontraron $($roots.Count) procesos raíz." }

    $rootProcess = Get-Process -Id ([int]$roots[0].ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $rootProcess -or $rootProcess.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "La ventana de WezTerm del workload Idle no está disponible para solicitar la salida de Yazi."
    }
    # Usar un contenedor mutable permite ampliar el conjunto si un proceso del
    # árbol crea un descendiente mientras esperamos su salida.
    $WorkloadState.ProcessIds = @(Get-ProcessTreeIds @([int]$roots[0].ProcessId) $ProtectedIds)
    $targetState = @{ Ids = $WorkloadState.ProcessIds }
    $quitSent = [TenchyShellPerformanceOrchestratorInputV2]::TrySendQuit($rootProcess.MainWindowHandle)
    if (-not $quitSent) {
        Write-Warning "No se pudo verificar el foco de la ventana de Yazi; se usará CloseMainWindow() como fallback cooperativo."
        if (-not $rootProcess.CloseMainWindow()) {
            throw "No se pudo enviar 'q' ni solicitar el cierre cooperativo de la ventana de prueba."
        }
    } else {
        Write-Host "Salida normal de Yazi solicitada mediante Win32 en la ventana dirigida."
    }

    try {
        Wait-Condition {
            $targetState.Ids = @(Get-ProcessTreeIds $targetState.Ids $ProtectedIds)
            $WorkloadState.ProcessIds = $targetState.Ids
            @(Get-RemainingProcessDetails $targetState.Ids).Count -eq 0
        } 10 "WezTerm/Yazi no terminó normalmente."
    } catch {
        $remaining = @(Get-RemainingProcessDetails $targetState.Ids)
        $details = if ($remaining.Count -eq 0) { "ninguno (el árbol terminó tras el timeout)" } else { $remaining -join "; " }
        throw "WezTerm/Yazi no terminó normalmente dentro del plazo; no se forzó ningún proceso. Restantes: $details"
    }
}

function Get-SessionTelemetry([datetime]$StartedAt, [string]$Destination) {
    $records = [Collections.Generic.List[object]]::new()
    $sessionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $liveBenchmarkDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $liveBenchmarkDirectory -Filter "live-*.jsonl" -File)) {
            foreach ($line in @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue)) {
                try {
                    $record = $line | ConvertFrom-Json
                    if ($record.name -eq "session_started" -and [datetime]$record.timestamp -ge $StartedAt) { [void]$sessionIds.Add([string]$record.sessionId) }
                    if ($sessionIds.Contains([string]$record.sessionId)) { [void]$records.Add($line) }
                } catch { }
            }
        }
    }
    if ($records.Count -gt 0) { $records | Set-Content -LiteralPath $Destination -Encoding utf8 }
    return @($sessionIds)
}

function Get-ResourceAnalysis([string]$CapturePath, [string]$RunId) {
    $capture = Get-Content -LiteralPath $CapturePath -Raw | ConvertFrom-Json
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($run in @($capture.runs)) {
        foreach ($sample in @($run.samples)) {
            foreach ($process in @($sample.processes)) {
                [void]$rows.Add([pscustomobject]@{ id = [int]$process.id; name = [string]$process.name; capturedAt = [datetime]$sample.capturedAt; privateBytes = [long]$process.privateBytes; workingSetBytes = [long]$process.workingSetBytes; handles = [int]$process.handles; threads = [int]$process.threads; cpuPercent = if ($null -eq $process.cpuPercent) { $null } else { [double]$process.cpuPercent } })
            }
        }
    }
    $deltas = foreach ($group in @($rows | Group-Object id)) {
        $series = @($group.Group | Sort-Object capturedAt)
        if ($series.Count -lt 2) { continue }
        $first = $series[0]; $last = $series[$series.Count - 1]; $increasingIntervals = 0
        for ($index = 1; $index -lt $series.Count; $index++) {
            if ($series[$index].privateBytes -gt $series[$index - 1].privateBytes -or $series[$index].handles -gt $series[$index - 1].handles -or $series[$index].threads -gt $series[$index - 1].threads) { $increasingIntervals++ }
        }
        [ordered]@{
            pid = [int]$last.id; process = $last.name; samples = $series.Count
            first = [ordered]@{ privateBytes = $first.privateBytes; workingSetBytes = $first.workingSetBytes; handles = $first.handles; threads = $first.threads }
            last = [ordered]@{ privateBytes = $last.privateBytes; workingSetBytes = $last.workingSetBytes; handles = $last.handles; threads = $last.threads }
            delta = [ordered]@{ privateBytes = $last.privateBytes - $first.privateBytes; workingSetBytes = $last.workingSetBytes - $first.workingSetBytes; handles = $last.handles - $first.handles; threads = $last.threads - $first.threads; cpuPercent = if ($null -eq $last.cpuPercent -or $null -eq $first.cpuPercent) { $null } else { $last.cpuPercent - $first.cpuPercent } }
            increasingIntervals = $increasingIntervals
            suspicious = $increasingIntervals -ge 3 -and (($last.privateBytes - $first.privateBytes) -ge 1MB -or ($last.handles - $first.handles) -ge 10 -or ($last.threads - $first.threads) -ge 2)
        }
    }
    return [ordered]@{ runId = $RunId; generatedAt = (Get-Date).ToUniversalTime().ToString("O"); method = "deltas inicial-final y crecimiento sostenido por proceso"; processes = @($deltas); suspects = @($deltas | Where-Object suspicious) }
}

function Copy-RunArtifacts([string]$RunDirectory, [datetime]$StartedAt, [string]$RunId, [string]$CapturePath) {
    Copy-SessionLog $RunDirectory $RunId
    $telemetryPath = Join-Path $RunDirectory "live-telemetry.jsonl"
    $sessionIds = @(Get-SessionTelemetry $StartedAt $telemetryPath)
    $analysisPath = Join-Path $RunDirectory "resource-analysis.json"
    Get-ResourceAnalysis $CapturePath $RunId | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $analysisPath -Encoding utf8
    return [ordered]@{ runId = $RunId; sessionIds = @($sessionIds); log = if (Test-Path -LiteralPath (Join-Path $RunDirectory "tenchyshell.log")) { "tenchyshell.log" } else { $null }; telemetry = if (Test-Path -LiteralPath $telemetryPath) { "live-telemetry.jsonl" } else { $null }; analysis = "resource-analysis.json" }
}

function Copy-SessionLog([string]$RunDirectory, [string]$RunId) {
    if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) { return }
    $marker = "[sessionId=$RunId]"
    $lines = @(Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue | Where-Object { $_.IndexOf($marker, [StringComparison]::Ordinal) -ge 0 })
    if ($lines.Count -gt 0) { $lines | Set-Content -LiteralPath (Join-Path $RunDirectory "tenchyshell.log") -Encoding utf8 }
}

function Invoke-DiagnosticCapture([string]$Phase, $Manifest, [string]$RunDirectory, [string]$RunId) {
    $settings = Get-CaptureSettings $Phase
    $shellExitAfterSeconds = [Math]::Min(3600, $StabilizationSeconds + (Get-CollectorDurationSeconds $settings) + 60)
    $shellProcess = $null; $idleInitialIds = @(); $idleWorkloadState = @{ ProcessIds = @() }; $idleStarted = $false; $cleanupAttempted = $false; $cleanupCompleted = $false; $cleanupError = $null; $cleanupRemaining = @(); $capturePath = $null; $beforeCapturePaths = @()
    $errors = [Collections.Generic.List[string]]::new(); $startedAt = [datetime]::UtcNow
    $stdoutPath = Join-Path $RunDirectory "$Phase-stdout.log"; $stderrPath = Join-Path $RunDirectory "$Phase-stderr.log"
    $measureOutputDirectory = Join-Path $RunDirectory "capture"
    New-Item -ItemType Directory -Path $measureOutputDirectory -Force | Out-Null
    try {
        $tools = Get-ToolProcesses; $excludedProcessIds = @($tools.Id)
        if ($tools.Count -gt 0) {
            if ($SmokeTest) { Write-Warning "El smoke test excluirá procesos WezTerm/Yazi ya abiertos: $($tools.ProcessName -join ', ')." }
            else { throw "La captura oficial debe comenzar sin WezTerm/Yazi; encontrados: $($tools.ProcessName -join ', ')." }
        }
        $voluntary = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $benchmarkSessionId -and $voluntaryProcessNames -contains $_.ProcessName.ToLowerInvariant() })
        if ($voluntary.Count -gt 0) {
            $names = @($voluntary.ProcessName | Sort-Object -Unique) -join ', '
            if ($SmokeTest) { Write-Warning "El smoke test continuará con aplicaciones abiertas: $names." } else { throw "Cierra aplicaciones voluntarias antes de medir: $names." }
        }
        if (@(Get-TenchyShellProcesses).Count -gt 0) { throw "Ya existe una instancia de TenchyShell." }
        & $shellExecutable --check $withoutExplorerConfiguration
        if ($LASTEXITCODE -ne 0) { throw "La publicación no superó --check." }
        $shellProcess = Start-Process -FilePath $shellExecutable -ArgumentList @("--automated-benchmark", "--without-explorer", "--session-id", $RunId, "--exit-after-seconds", $shellExitAfterSeconds, $withoutExplorerConfiguration) -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
        Wait-TenchyShellReady $shellProcess 35
        if (-not (Test-DesktopShellSurfaceAbsent)) { throw "Se detectó una superficie de shell activa durante la captura." }
        if ($Phase -eq "Idle") {
            # Capturar todos los PIDs de la sesión antes de crear el workload evita
            # que una limpieza de emergencia toque procesos preexistentes, aunque
            # no sean WezTerm/Yazi (por ejemplo, OpenConsole.exe).
            $idleInitialIds = @(Get-SessionProcessIds)
            $idleInitialIds = @(Start-IdleTools $idleInitialIds)
            $idleStarted = $true
        }
        if ($StabilizationSeconds -gt 0) { Write-Host "Estabilización automática: $StabilizationSeconds segundos. No interactúes con el equipo."; Start-Sleep -Seconds $StabilizationSeconds }
        $beforeCapturePaths = @(Get-ChildItem -LiteralPath $measureOutputDirectory -Recurse -Filter "*.json" -File | Select-Object -ExpandProperty FullName)
        $measureArguments = @{
            Scenario = "TenchyShell"; Phase = $Phase; MainProcessId = [int]$shellProcess.Id; ExcludedProcessIds = $excludedProcessIds
            Repetitions = $settings.Repetitions; SamplesPerRepetition = $settings.SamplesPerRepetition; IntervalMilliseconds = $settings.IntervalMilliseconds
            WarmupSeconds = $settings.WarmupSeconds; InterRepetitionSeconds = $settings.InterRepetitionSeconds; WorkflowLaunchSecond = $settings.WorkflowLaunchSecond
            WorkflowCloseSecond = $settings.WorkflowCloseSecond; WorkflowVerifyClosedSecond = $settings.WorkflowVerifyClosedSecond; StressActionSeconds = $settings.StressActionSeconds
            OutputDirectory = $measureOutputDirectory
        }
        if ($SmokeTest) { $measureArguments.SmokeTest = $true; $measureArguments.AllowExistingToolsForSmoke = $true; if ($Phase -eq "TenchyShellStress") { $measureArguments.ManualStressActions = $true } } else { $measureArguments.BatchId = $BatchId }
        try { & $measureScript @measureArguments } catch { $errors.Add($_.Exception.Message) }
        $captureFiles = @(Get-ChildItem -LiteralPath $measureOutputDirectory -Recurse -Filter "*.json" -File | Where-Object { $_.FullName -notin $beforeCapturePaths })
        if ($captureFiles.Count -ne 1) { $errors.Add("El recolector debía crear exactamente un JSON y creó $($captureFiles.Count).") } else { $capturePath = $captureFiles[0].FullName }
        if ($idleStarted -and -not $cleanupAttempted) {
            $cleanupAttempted = $true
            try {
                Close-OwnedToolsNormally $idleInitialIds $idleWorkloadState
                $cleanupCompleted = $true
            } catch {
                $cleanupError = $_.Exception.Message
                $cleanupRemaining = @(Get-RemainingProcessDetails $idleWorkloadState.ProcessIds)
                $errors.Add("Limpieza incompleta del workload Idle: $cleanupError")
            }
        }
        if (-not $shellProcess.WaitForExit([Math]::Min(120000, ($shellExitAfterSeconds + 15) * 1000))) { $errors.Add("Timeout: TenchyShell no terminó dentro del plazo; no se iniciará ninguna recuperación automática.") } elseif ($shellProcess.ExitCode -ne 0) { $errors.Add("TenchyShell terminó con código $($shellProcess.ExitCode).") }
        if ($null -ne $capturePath) {
            $artifacts = Copy-RunArtifacts $RunDirectory $startedAt $RunId $capturePath
            $data = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
            if (-not $cleanupCompleted) { $data.official = $false }
            $artifacts["cleanupAttempted"] = $cleanupAttempted
            $artifacts["cleanupCompleted"] = $cleanupCompleted
            $artifacts["cleanupError"] = $cleanupError
            $artifacts["remainingProcesses"] = @($cleanupRemaining)
            $artifacts["officialUsable"] = [bool]$data.official -and [bool]$data.valid -and $cleanupCompleted
            $data | Add-Member -NotePropertyName diagnostic -NotePropertyValue $artifacts -Force
            $data | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $capturePath -Encoding utf8
        }
    } catch { $errors.Add($_.Exception.Message) }
    finally {
        if ($idleStarted -and -not $cleanupAttempted) {
            $cleanupAttempted = $true
            try {
                Close-OwnedToolsNormally $idleInitialIds $idleWorkloadState
                $cleanupCompleted = $true
            } catch {
                $cleanupError = $_.Exception.Message
                $cleanupRemaining = @(Get-RemainingProcessDetails $idleWorkloadState.ProcessIds)
                $errors.Add("Limpieza incompleta del workload Idle: $cleanupError")
            }
        }
        if ($null -ne $shellProcess -and -not $shellProcess.HasExited) { $errors.Add("Timeout: TenchyShell no terminó; no se forzó ningún proceso y la sesión permanece sin superficie de shell.") }
        if ($null -ne $shellProcess) { $shellProcess.Dispose() }
        if ($null -ne $capturePath) {
            Copy-SessionLog $RunDirectory $RunId
            [void](Get-SessionTelemetry $startedAt (Join-Path $RunDirectory "live-telemetry.jsonl"))
            if (-not (Test-Path -LiteralPath (Join-Path $RunDirectory "tenchyshell.log"))) { $errors.Add("No se encontró una copia del log persistente de TenchyShell.") }
            if (-not (Test-Path -LiteralPath (Join-Path $RunDirectory "resource-analysis.json"))) { $errors.Add("No se generó el análisis de deltas de recursos.") }
        }
        [ordered]@{ runId = $RunId; phase = $Phase; startedAt = $startedAt.ToString("O"); completedAt = (Get-Date).ToUniversalTime().ToString("O"); shellSurfaceAbsentAtEnd = Test-DesktopShellSurfaceAbsent; completed = $errors.Count -eq 0; errors = @($errors); cleanupAttempted = $cleanupAttempted; cleanupCompleted = $cleanupCompleted; cleanupError = $cleanupError; remainingProcesses = @($cleanupRemaining); manualRecoveryRequired = $true } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunDirectory "$Phase-run-status.json") -Encoding utf8
    }
    if ($errors.Count -gt 0) { throw "Captura diagnóstica no oficial o incompleta: $(@($errors) -join ' | ')" }
    Write-Host "Captura diagnóstica válida: TenchyShell / $Phase. Resultados: $RunDirectory"
}

if ($PlanOnly) {
    if ($SmokeTest) { $matrix | Select-Object Scenario, Phase } else { [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "Idle" } }
    exit 0
}
if (-not [string]::IsNullOrWhiteSpace($RequiredUser) -and $env:USERNAME -ne $RequiredUser) { throw "Este benchmark está reservado al usuario '$RequiredUser'; sesión actual: '$env:USERNAME'." }
$manifest = Read-ReleaseManifest
$runId = "$(Get-Date -Format yyyyMMdd-HHmmss)-$([Guid]::NewGuid().ToString('N'))"
$runDirectory = Join-Path $benchmarkRoot $runId
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
[ordered]@{ runId = $runId; batchId = $BatchId; warning = "La recuperación posterior es manual; este benchmark no inicia ni restaura la superficie de shell del sistema."; startedAt = (Get-Date).ToUniversalTime().ToString("O"); manifestCommit = [string]$manifest.gitCommit } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory "run.json") -Encoding utf8
Write-Warning "Este benchmark dejará la sesión sin Explorer y no lo restaurará. Ejecuta únicamente en una VM o usuario secundario; la recuperación posterior es manual."
if ($SmokeTest) { foreach ($capture in $matrix) { Invoke-DiagnosticCapture $capture.Phase $manifest $runDirectory $runId } } else { Invoke-DiagnosticCapture "Idle" $manifest $runDirectory $runId }
Write-Warning "La sesión termina sin recuperación automática. Si necesitas recuperar Windows, usa manualmente el hotkey configurado o tu procedimiento de VM/usuario secundario."
Write-Host "Benchmark diagnóstico completo. Resultados: $runDirectory"
