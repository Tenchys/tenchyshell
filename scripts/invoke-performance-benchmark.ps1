[CmdletBinding()]
param(
    [string]$BatchId = "",

    [switch]$SmokeTest,

    [ValidateRange(-1, 1800)]
    [int]$StabilizationSeconds = -1,

    [string]$RequiredUser = "TenchyBenchmark",

    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$measureScript = Join-Path $PSScriptRoot "measure-performance.ps1"
$summarizeScript = Join-Path $PSScriptRoot "summarize-performance.ps1"
$publishDirectory = Join-Path $repoRoot "publish\TenchyShell\Release\win-x64"
$shellExecutable = Join-Path $publishDirectory "TenchyShell.exe"
$normalConfiguration = Join-Path $publishDirectory "TenchyShell.example.toml"
$withoutExplorerConfiguration = Join-Path $publishDirectory "TenchyShell.without-explorer.example.toml"
$releaseManifestPath = Join-Path $publishDirectory "benchmark-release.json"
$benchmarkRoot = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v3"
$liveBenchmarkDirectory = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\live"
$logPath = Join-Path $env:LOCALAPPDATA "TenchyShell\logs\tenchyshell.log"
$benchmarkSessionId = (Get-Process -Id $PID).SessionId
$toolNames = @("wezterm", "wezterm-gui", "yazi")
$voluntaryProcessNames = @("brave", "chrome", "firefox", "msedge", "opera", "dropbox", "onedrive")
$matrix = @(
    [pscustomobject]@{ Scenario = "Explorer"; Phase = "Idle" },
    [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "Idle" },
    [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "CommonWorkflow" },
    [pscustomobject]@{ Scenario = "Explorer"; Phase = "CommonWorkflow" },
    [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "TenchyShellStress" }
)

if (-not $SmokeTest -and [string]::IsNullOrWhiteSpace($BatchId)) {
    throw "El benchmark oficial requiere -BatchId."
}
if (-not [string]::IsNullOrWhiteSpace($BatchId) -and $BatchId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw "BatchId solo admite letras ASCII, números, punto, guion y guion bajo (máximo 64 caracteres)."
}
if ($StabilizationSeconds -lt 0) {
    $StabilizationSeconds = if ($SmokeTest) { 0 } else { 300 }
}

if (-not ("TenchyShellPerformanceOrchestratorInput" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static class TenchyShellPerformanceOrchestratorInput
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    public static bool TrySendQuit(IntPtr window)
    {
        if (window == IntPtr.Zero || !SetForegroundWindow(window)) return false;
        Thread.Sleep(150);
        if (GetForegroundWindow() != window) return false;
        const byte Q = 0x51;
        const uint KeyUp = 0x0002;
        keybd_event(Q, 0, 0, UIntPtr.Zero);
        keybd_event(Q, 0, KeyUp, UIntPtr.Zero);
        return true;
    }

    public static int GetShellTrayProcessId()
    {
        var window = FindWindow("Shell_TrayWnd", null);
        if (window == IntPtr.Zero) return 0;
        uint processId;
        return GetWindowThreadProcessId(window, out processId) == 0 ? 0 : checked((int)processId);
    }
}
'@
}

function Get-ToolProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq $benchmarkSessionId -and
            $toolNames -contains $_.ProcessName.ToLowerInvariant()
    })
}

function Get-ExplorerProcesses {
    return @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object SessionId -eq $benchmarkSessionId)
}

function Get-TenchyShellProcesses {
    return @(Get-Process -Name TenchyShell -ErrorAction SilentlyContinue | Where-Object SessionId -eq $benchmarkSessionId)
}

function Test-ShellTrayPresent {
    return [TenchyShellPerformanceOrchestratorInput]::GetShellTrayProcessId() -gt 0
}

function Get-ExplorerCoreProcessId {
    $processId = [TenchyShellPerformanceOrchestratorInput]::GetShellTrayProcessId()
    if ($processId -le 0) { throw "Shell_TrayWnd no está disponible para identificar el núcleo de Explorer." }
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.SessionId -ne $benchmarkSessionId -or $process.ProcessName -ne "explorer") {
        throw "Shell_TrayWnd pertenece a un proceso inesperado o de otra sesión (PID $processId)."
    }
    return [int]$processId
}

function Wait-Condition([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$FailureMessage) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Wait-ExplorerStable([int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableSince = $null
    $stableProcessId = 0
    do {
        try { $processId = Get-ExplorerCoreProcessId } catch { $processId = 0 }
        if ($processId -gt 0) {
            if ($null -eq $stableSince -or $stableProcessId -ne $processId) {
                $stableSince = [DateTime]::UtcNow
                $stableProcessId = $processId
            }
            elseif (([DateTime]::UtcNow - $stableSince).TotalSeconds -ge 2) { return }
        } else {
            $stableSince = $null
            $stableProcessId = 0
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Explorer no mantuvo un Shell_TrayWnd con propietario identificable y estable dentro del plazo."
}

function Wait-TenchyShellReady([Diagnostics.Process]$Process, [int]$TimeoutSeconds = 35) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "TenchyShell terminó antes de alcanzar el estado sin Explorer (código $($Process.ExitCode))."
        }
        $ownedProcess = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
        if ($null -ne $ownedProcess -and $ownedProcess.SessionId -eq $benchmarkSessionId -and
            $ownedProcess.ProcessName -eq "TenchyShell" -and -not (Test-ShellTrayPresent)) {
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "TenchyShell no alcanzó el estado estable sin Explorer."
}

function Get-NextOfficialCapture {
    $batchPath = Join-Path $benchmarkRoot $BatchId
    $captures = @()
    if (Test-Path -LiteralPath $batchPath -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $batchPath -Filter "*.json" -File)) {
            try { $data = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
            catch { throw "El lote contiene un JSON ilegible: $($file.FullName). $($_.Exception.Message)" }
            if ([int]$data.schemaVersion -ne 3 -or -not [bool]$data.official) {
                throw "El lote oficial contiene un archivo que no es una captura oficial de esquema 3: $($file.FullName)."
            }
            if ([string]$data.batchId -ne $BatchId) {
                throw "El lote contiene una captura con BatchId incompatible: $($file.FullName)."
            }
            if (-not [bool]$data.valid) {
                throw "El lote contiene una captura inválida. Revísala y usa un BatchId nuevo o resuelve el hallazgo: $($file.FullName)."
            }
            $captures += [pscustomobject]@{ Key = "$($data.scenario)/$($data.phase)"; File = $file.FullName }
        }
    }

    $duplicates = @($captures | Group-Object Key | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "El lote contiene capturas duplicadas: $($duplicates.Name -join ', ')."
    }
    foreach ($entry in $matrix) {
        $key = "$($entry.Scenario)/$($entry.Phase)"
        if ($key -notin @($captures.Key)) { return $entry }
    }
    return $null
}

function Get-CaptureSettings([string]$Phase) {
    if ($SmokeTest) {
        return [ordered]@{
            Repetitions = 1
            SamplesPerRepetition = 7
            IntervalMilliseconds = 1000
            WarmupSeconds = 0
            InterRepetitionSeconds = 0
            WorkflowLaunchSecond = 1
            WorkflowCloseSecond = 3
            WorkflowVerifyClosedSecond = 5
            StressActionSeconds = @(1, 2, 3, 4, 5)
        }
    }
    return [ordered]@{
        Repetitions = 5
        SamplesPerRepetition = 30
        IntervalMilliseconds = 1000
        WarmupSeconds = 10
        InterRepetitionSeconds = 15
        WorkflowLaunchSecond = 5
        WorkflowCloseSecond = 20
        WorkflowVerifyClosedSecond = 25
        StressActionSeconds = @(5, 10, 15, 20, 25)
    }
}

function Get-CollectorDurationSeconds($Settings) {
    $measurement = [double]$Settings.Repetitions * [double]$Settings.SamplesPerRepetition * [double]$Settings.IntervalMilliseconds / 1000
    $rests = [Math]::Max(0, [int]$Settings.Repetitions - 1) * [int]$Settings.InterRepetitionSeconds
    return [int][Math]::Ceiling([int]$Settings.WarmupSeconds + $measurement + $rests)
}

function Read-ReleaseManifest {
    foreach ($path in @($shellExecutable, $normalConfiguration, $withoutExplorerConfiguration, $releaseManifestPath, $measureScript)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Falta el artefacto requerido: $path" }
    }
    $manifest = Get-Content -LiteralPath $releaseManifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or $manifest.configuration -ne "Release" -or $manifest.runtime -ne "win-x64") {
        throw "benchmark-release.json no describe una publicación Release win-x64 compatible."
    }
    $safeRepoRoot = $repoRoot.Replace('\', '/')
    $commit = (& git -c "safe.directory=$safeRepoRoot" -C $repoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { throw "No se pudo identificar el commit actual." }
    $dirty = [bool](& git -c "safe.directory=$safeRepoRoot" -C $repoRoot status --porcelain 2>$null)
    if (-not $SmokeTest -and $dirty) { throw "El benchmark oficial requiere un árbol Git limpio." }
    if ([string]$manifest.gitCommit -ne [string]$commit) {
        throw "La publicación no corresponde al commit actual. Ejecuta .\scripts\publish.ps1 para actualizarla."
    }
    if (-not $SmokeTest -and [bool]$manifest.gitDirty) {
        throw "La publicación oficial no procede de un árbol Git limpio. Ejecuta .\scripts\publish.ps1 después del commit."
    }
    return $manifest
}

function Start-IdleTools {
    $existing = Get-ToolProcesses
    $initialIds = @($existing.Id)
    Start-Process -FilePath "wezterm-gui.exe" -ArgumentList @("start", "--always-new-process", "--class", "TenchyShellBenchmarkIdle", "--", "yazi.exe") | Out-Null
    Wait-Condition {
        $tools = @(Get-ToolProcesses | Where-Object { $_.Id -notin $initialIds })
        @($tools | Where-Object ProcessName -eq "yazi").Count -ge 1 -and
            @($tools | Where-Object { $_.ProcessName -in @("wezterm", "wezterm-gui") }).Count -ge 1
    } 15 "No aparecieron WezTerm y Yazi dentro del tiempo esperado."
    return $initialIds
}

function Close-OwnedToolsNormally([int[]]$InitialIds) {
    $owned = @(Get-ToolProcesses | Where-Object { $_.Id -notin $InitialIds })
    $windows = @($owned | Where-Object {
        if ($_.ProcessName -notin @("wezterm", "wezterm-gui") -or $_.MainWindowHandle -eq [IntPtr]::Zero) {
            return $false
        }
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue
        return $null -ne $cim -and [string]$cim.CommandLine -match "--class\s+TenchyShellBenchmarkIdle"
    })
    if ($windows.Count -eq 0) {
        return
    }
    if ($windows.Count -ne 1) {
        throw "Se esperaba una ventana propia de WezTerm para cerrar Yazi y se encontraron $($windows.Count)."
    }
    if (-not [TenchyShellPerformanceOrchestratorInput]::TrySendQuit($windows[0].MainWindowHandle)) {
        if (-not $windows[0].CloseMainWindow()) {
            throw "No se pudo enfocar exclusivamente la ventana propia de WezTerm para solicitar 'q'."
        }
    }
    $targetRootIds = @($windows.Id)
    $targetIds = @($targetRootIds) + @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -in $targetRootIds } | Select-Object -ExpandProperty ProcessId)
    try {
        Wait-Condition {
            @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                Where-Object { $_.ProcessId -in $targetIds }).Count -eq 0
        } 10 "WezTerm/Yazi no terminó normalmente; no se forzó el cierre de procesos."
    } catch {
        if (-not $SmokeTest) { throw }
        Write-Warning "El workload Idle no aceptó el cierre cooperativo; se limpiarán únicamente sus PIDs de prueba ($($targetIds -join ', '))."
        foreach ($targetId in $targetIds) {
            try { Stop-Process -Id ([int]$targetId) -Force -ErrorAction SilentlyContinue } catch { }
        }
        try {
            Wait-Condition {
                @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object { $_.ProcessId -in $targetIds }).Count -eq 0
            } 10 ""
        } catch { }
    }
}

function Stop-TestOwnedProcesses([int[]]$ProtectedIds) {
    $escapedShell = [Regex]::Escape($shellExecutable)
    $owned = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -notin $ProtectedIds -and
        [string]$_.CommandLine -match $escapedShell
    } | Sort-Object ProcessId -Descending)
    foreach ($process in $owned) {
        try { Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Set-CaptureOrchestration(
    [string]$Path,
    $Manifest,
    [bool]$IdleManaged,
    [bool]$LiveTelemetryUnchanged,
    [string[]]$Errors) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $data | Add-Member -NotePropertyName orchestration -NotePropertyValue ([pscustomobject]@{
        mode = "Automated"
        version = 2
        shellLifecycleManaged = $data.scenario -eq "TenchyShell"
        idleToolManaged = $IdleManaged
        liveTelemetryUnchanged = $LiveTelemetryUnchanged
        releaseGitCommit = [string]$Manifest.gitCommit
        completed = $Errors.Count -eq 0
        errors = @($Errors)
    }) -Force
    if ($Errors.Count -gt 0) {
        $data.valid = $false
        $data.invalidReasons = @($data.invalidReasons) + @($Errors)
    }
    $data | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-LiveTelemetrySnapshot {
    $snapshot = @{}
    if (Test-Path -LiteralPath $liveBenchmarkDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $liveBenchmarkDirectory -Filter "live-*.jsonl" -File)) {
            $null = $snapshot[$file.FullName] = [long]$file.Length
        }
    }
    Write-Output -NoEnumerate -InputObject $snapshot
}

function Test-LiveTelemetryUnchanged([hashtable]$Before) {
    $after = Get-LiveTelemetrySnapshot
    if ($Before.Count -ne $after.Count) { return $false }
    foreach ($path in $Before.Keys) {
        if (-not (@($after.Keys) -contains $path) -or [long]$after[$path] -ne [long]$Before[$path]) { return $false }
    }
    return $true
}

function Ensure-ExplorerRecovery {
    if (-not (Test-ShellTrayPresent)) {
        Start-Process -FilePath "explorer.exe" | Out-Null
    }
    try { Wait-ExplorerStable 20 }
    catch { throw "No se pudo restaurar explorer.exe de forma estable. Usa Ctrl+Alt+Supr para recuperación. $($_.Exception.Message)" }
}

function Invoke-AutomatedCapture([string]$Scenario, [string]$Phase, $Manifest) {
    $settings = Get-CaptureSettings $Phase
    $collectorDuration = Get-CollectorDurationSeconds $settings
    $shellExitAfterSeconds = [Math]::Min(3600, $StabilizationSeconds + $collectorDuration + 60)
    $shellProcess = $null
    $idleInitialIds = @()
    $protectedToolIds = @()
    $idleManaged = $false
    $newCapturePath = $null
    $errors = [Collections.Generic.List[string]]::new()
    $liveTelemetryBefore = Get-LiveTelemetrySnapshot
    $liveTelemetryUnchanged = $true
    $logOffset = if (Test-Path -LiteralPath $logPath -PathType Leaf) { (Get-Content -LiteralPath $logPath -Raw -Encoding UTF8).Length } else { 0 }
    $outputDirectory = if ($SmokeTest) { $benchmarkRoot } else { Join-Path $benchmarkRoot $BatchId }
    if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $beforeFiles = @((Get-ChildItem -LiteralPath $outputDirectory -Filter "*.json" -File -ErrorAction SilentlyContinue).FullName)

    try {
        $tools = Get-ToolProcesses
        $excludedProcessIds = @($tools.Id)
        $protectedToolIds = $excludedProcessIds
        if ($tools.Count -gt 0) {
            if ($SmokeTest) {
                Write-Warning "El smoke test excluirá del lote los procesos WezTerm/Yazi ya abiertos: $($tools.ProcessName -join ', ')."
            } else {
                throw "La captura oficial debe comenzar sin WezTerm/Yazi; encontrados: $($tools.ProcessName -join ', ')."
            }
        }
        $voluntary = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.SessionId -eq $benchmarkSessionId -and
                $voluntaryProcessNames -contains $_.ProcessName.ToLowerInvariant()
        })
        if ($voluntary.Count -gt 0) {
            $voluntaryNames = @($voluntary.ProcessName | Sort-Object -Unique) -join ', '
            if ($SmokeTest) {
                Write-Warning "El smoke test continuará con aplicaciones voluntarias abiertas: $voluntaryNames."
            } else {
                throw "Cierra aplicaciones voluntarias antes de medir: $voluntaryNames."
            }
        }
        if (@(Get-TenchyShellProcesses).Count -gt 0) { throw "Ya existe una instancia de TenchyShell." }
        if (-not (Test-ShellTrayPresent)) { Ensure-ExplorerRecovery }
        Wait-ExplorerStable 20

        & $shellExecutable --check $normalConfiguration
        if ($LASTEXITCODE -ne 0) { throw "La publicación no superó --check." }

        if ($Scenario -eq "TenchyShell") {
            $shellArguments = @(
                "--automated-benchmark",
                "--without-explorer",
                "--exit-after-seconds", $shellExitAfterSeconds,
                $withoutExplorerConfiguration
            )
            $shellProcess = Start-Process -FilePath $shellExecutable -ArgumentList $shellArguments -WindowStyle Hidden -PassThru
            Wait-TenchyShellReady $shellProcess 35
        }

        if ($Phase -eq "Idle") {
            $idleInitialIds = @(Start-IdleTools)
            $idleManaged = $true
        }

        if ($StabilizationSeconds -gt 0) {
            Write-Host "Estabilización automática: $StabilizationSeconds segundos. No interactúes con el equipo."
            Start-Sleep -Seconds $StabilizationSeconds
        }

        $measureArguments = @{
            Scenario = $Scenario
            Phase = $Phase
            MainProcessId = if ($Scenario -eq "TenchyShell") { [int]$shellProcess.Id } else { Get-ExplorerCoreProcessId }
            ExcludedProcessIds = $excludedProcessIds
            Repetitions = $settings.Repetitions
            SamplesPerRepetition = $settings.SamplesPerRepetition
            IntervalMilliseconds = $settings.IntervalMilliseconds
            WarmupSeconds = $settings.WarmupSeconds
            InterRepetitionSeconds = $settings.InterRepetitionSeconds
            WorkflowLaunchSecond = $settings.WorkflowLaunchSecond
            WorkflowCloseSecond = $settings.WorkflowCloseSecond
            WorkflowVerifyClosedSecond = $settings.WorkflowVerifyClosedSecond
            StressActionSeconds = $settings.StressActionSeconds
        }
        if ($SmokeTest) {
            $measureArguments.SmokeTest = $true
            $measureArguments.AllowExistingToolsForSmoke = $true
            if ($Phase -eq "TenchyShellStress") { $measureArguments.ManualStressActions = $true }
        }
        else { $measureArguments.BatchId = $BatchId }

        try { & $measureScript @measureArguments }
        catch { $errors.Add($_.Exception.Message) }

        $afterFiles = @((Get-ChildItem -LiteralPath $outputDirectory -Filter "*.json" -File -ErrorAction SilentlyContinue).FullName)
        $newFiles = @($afterFiles | Where-Object { $_ -notin $beforeFiles })
        if ($newFiles.Count -ne 1) { $errors.Add("El recolector debía crear exactamente un JSON y creó $($newFiles.Count).") }
        else { $newCapturePath = $newFiles[0] }

        if ($idleManaged) {
            try { Close-OwnedToolsNormally $idleInitialIds }
            catch { $errors.Add($_.Exception.Message) }
        }

        if ($Scenario -eq "TenchyShell") {
            if (-not $shellProcess.WaitForExit([Math]::Min(120000, ($shellExitAfterSeconds + 15) * 1000))) {
                $errors.Add("TenchyShell no terminó limpiamente dentro del plazo acotado.")
            } elseif ($shellProcess.ExitCode -ne 0) {
                $errors.Add("TenchyShell terminó con código $($shellProcess.ExitCode).")
            }
            try { Wait-ExplorerStable 20 }
            catch { $errors.Add($_.Exception.Message) }
            if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                $log = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
                $newLog = if ($logOffset -le $log.Length) { $log.Substring([int]$logOffset) } else { "" }
                if ($newLog.IndexOf("TenchyShell finalizado; se liberaron hotkeys y recursos Win32.", [StringComparison]::Ordinal) -lt 0) {
                    $errors.Add("El log no confirmó la liberación limpia de hotkeys y recursos Win32.")
                }
            } else {
                $errors.Add("No se encontró el log de TenchyShell para validar el cierre limpio.")
            }
        }
    } catch {
        $errors.Add($_.Exception.Message)
    } finally {
        $cleanupInitialIds = $protectedToolIds
        if (@(Get-ToolProcesses | Where-Object { $_.Id -notin $cleanupInitialIds }).Count -gt 0) {
            try { Close-OwnedToolsNormally $cleanupInitialIds }
            catch {
                if (-not (@($errors) -contains $_.Exception.Message)) { [void]$errors.Add($_.Exception.Message) }
            }
        }
        if ($null -ne $shellProcess -and -not $shellProcess.HasExited) {
            try {
                Stop-Process -Id $shellProcess.Id -Force -ErrorAction Stop
                [void]$shellProcess.WaitForExit(5000)
                $errors.Add("Se forzó el cierre del proceso TenchyShell propio durante la recuperación.")
            } catch {
                $errors.Add("No se pudo cerrar el proceso TenchyShell propio durante la recuperación: $($_.Exception.Message)")
            }
        }
        Stop-TestOwnedProcesses $protectedToolIds
        $liveTelemetryUnchanged = Test-LiveTelemetryUnchanged $liveTelemetryBefore
        if ($Scenario -eq "TenchyShell" -and -not $liveTelemetryUnchanged) {
            $liveTelemetryError = "--automated-benchmark creó o amplió archivos de telemetría live."
            if (-not (@($errors) -contains $liveTelemetryError)) { [void]$errors.Add($liveTelemetryError) }
        }
        try { Ensure-ExplorerRecovery }
        catch { $errors.Add($_.Exception.Message) }
        Set-CaptureOrchestration $newCapturePath $Manifest $idleManaged $liveTelemetryUnchanged @($errors)
        if ($null -ne $shellProcess) { $shellProcess.Dispose() }
    }

    if ($errors.Count -gt 0) {
        throw "Captura automatizada inválida: $(@($errors) -join ' | ')"
    }
    Write-Host "Captura automatizada válida: $Scenario / $Phase"
}

$plan = if ($SmokeTest) {
    @(
        [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "Idle" },
        [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "CommonWorkflow" },
        [pscustomobject]@{ Scenario = "TenchyShell"; Phase = "TenchyShellStress" }
    )
} else {
    $next = Get-NextOfficialCapture
    if ($null -eq $next) { @() } else { @($next) }
}

if ($PlanOnly) {
    $plan | Select-Object Scenario, Phase
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($RequiredUser) -and $env:USERNAME -ne $RequiredUser) {
    throw "Este benchmark está reservado al usuario '$RequiredUser'; sesión actual: '$env:USERNAME'."
}
$manifest = Read-ReleaseManifest

if ($plan.Count -eq 0) {
    $batchPath = Join-Path $benchmarkRoot $BatchId
    $summaryPath = Join-Path $batchPath "summary.md"
    & $summarizeScript -InputPath $batchPath -OutputPath $summaryPath
    Write-Host "El lote ya está completo. Informe: $summaryPath"
    exit 0
}

foreach ($capture in $plan) {
    Write-Host "Iniciando captura automatizada: $($capture.Scenario) / $($capture.Phase)"
    Invoke-AutomatedCapture $capture.Scenario $capture.Phase $manifest
}

if (-not $SmokeTest) {
    $next = Get-NextOfficialCapture
    if ($null -eq $next) {
        $batchPath = Join-Path $benchmarkRoot $BatchId
        $summaryPath = Join-Path $batchPath "summary.md"
        & $summarizeScript -InputPath $batchPath -OutputPath $summaryPath
        Write-Host "Benchmark oficial completo. Informe: $summaryPath"
    } else {
        Write-Host "Captura completada. Cierra sesión; en el próximo inicio ejecuta exactamente el mismo comando."
        Write-Host "Siguiente captura prevista: $($next.Scenario) / $($next.Phase)"
    }
}
