[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$summarizer = Join-Path $PSScriptRoot "summarize-performance.ps1"
$orchestrator = Join-Path $PSScriptRoot "invoke-performance-benchmark.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.Performance.Tests." + [Guid]::NewGuid().ToString("N"))

function Get-SyntheticTotals($Rows) {
    $items = @($Rows)
    $cpu = @($items | Where-Object { $null -ne $_.cpuPercent } | ForEach-Object cpuPercent)
    return [ordered]@{
        processCount = $items.Count
        cpuPercent = if ($cpu.Count -eq 0) { $null } else { [double](($cpu | Measure-Object -Sum).Sum) }
        privateBytes = [long](($items | ForEach-Object { [long]$_.privateBytes } | Measure-Object -Sum).Sum)
        workingSetBytes = [long](($items | ForEach-Object { [long]$_.workingSetBytes } | Measure-Object -Sum).Sum)
        handles = [int](($items | ForEach-Object { [int]$_.handles } | Measure-Object -Sum).Sum)
        threads = [int](($items | ForEach-Object { [int]$_.threads } | Measure-Object -Sum).Sum)
        readBytesPerSecond = if ($items.Count -eq 0) { $null } else { [double](($items | ForEach-Object { [double]$_.readBytesPerSecond } | Measure-Object -Sum).Sum) }
        writeBytesPerSecond = if ($items.Count -eq 0) { $null } else { [double](($items | ForEach-Object { [double]$_.writeBytesPerSecond } | Measure-Object -Sum).Sum) }
    }
}

function New-ProcessRow([int]$Id, [int]$ParentId, [string]$Name, [string]$Role, [double]$Cpu, [long]$PrivateBytes) {
    return [ordered]@{
        id = $Id
        parentId = $ParentId
        name = $Name
        role = $Role
        cpuPercent = $Cpu
        privateBytes = $PrivateBytes
        workingSetBytes = [long]($PrivateBytes * 0.8)
        handles = 10
        threads = 2
        readBytesPerSecond = 1MB
        writeBytesPerSecond = 0.5MB
        startTime = "2026-01-01T00:00:00.0000000Z"
    }
}

function New-Sample([string]$Scenario, [double]$CoreCpu, [double]$ToolCpu, [int]$CoreCount = 1) {
    $coreName = if ($Scenario -eq "Explorer") { "explorer" } else { "TenchyShell" }
    $processes = @()
    for ($index = 0; $index -lt $CoreCount; $index++) {
        $processes += New-ProcessRow (10 + $index) 1 $coreName "Core" $CoreCpu 100MB
    }
    if ($Scenario -eq "TenchyShell") {
        $processes += New-ProcessRow 11 10 "wezterm-gui" "Auxiliary" 0.5 30MB
        $processes += New-ProcessRow 12 11 "TenchyShell" "Auxiliary" 0.25 20MB
        $processes += New-ProcessRow 13 4 "TenchyShell.NotificationBridge" "Auxiliary" 0.25 15MB
    }
    $processes += New-ProcessRow 20 99 "wezterm-gui" "Tool" $ToolCpu 50MB
    $processes += New-ProcessRow 21 20 "yazi" "Tool" 0.5 25MB
    $coreRows = @($processes | Where-Object role -eq "Core")
    $auxiliaryRows = @($processes | Where-Object role -eq "Auxiliary")
    $toolRows = @($processes | Where-Object role -eq "Tool")
    return [ordered]@{
        capturedAt = (Get-Date).ToUniversalTime().ToString("O")
        mainProcessId = 10
        coreProcessCount = $CoreCount
        contaminationDetected = $false
        processes = @($processes)
        totalsByRole = [ordered]@{
            Core = Get-SyntheticTotals $coreRows
            Auxiliary = Get-SyntheticTotals $auxiliaryRows
            ShellSession = Get-SyntheticTotals @($coreRows + $auxiliaryRows)
            Tool = Get-SyntheticTotals $toolRows
            Total = Get-SyntheticTotals @($coreRows + $auxiliaryRows + $toolRows)
        }
        observer = [ordered]@{
            processId = 99
            cpuPercent = 0.2
            totalProcessorMilliseconds = 100
            privateBytes = 40MB
            workingSetBytes = 32MB
            samplingDurationMilliseconds = 12.5
        }
    }
}

function New-Capture([string]$Scenario, [string]$Phase, [bool]$Official = $true) {
    $repetitionCount = if ($Official) { 5 } else { 1 }
    $runs = @()
    for ($runIndex = 1; $runIndex -le $repetitionCount; $runIndex++) {
        $runs += [ordered]@{
            repetition = $runIndex
            valid = $true
            invalidReasons = @()
            events = @()
            samples = @(
                (New-Sample $Scenario 0 1),
                (New-Sample $Scenario $runIndex 2)
            )
        }
    }
    return [ordered]@{
        schemaVersion = 3
        batchId = if ($Official) { "synthetic-official" } else { "" }
        official = $Official
        valid = $true
        invalidReasons = @()
        scenario = $Scenario
        phase = $Phase
        capturedAt = (Get-Date).ToUniversalTime().ToString("O")
        settings = [ordered]@{
            repetitions = $repetitionCount
            samplesPerRepetition = 2
            intervalMilliseconds = 1000
            warmupSeconds = 10
            interRepetitionSeconds = 15
            workflowLaunchSecond = 5
            workflowCloseSecond = 20
            workflowVerifyClosedSecond = 25
            stressActionSeconds = @(5, 10, 15, 20, 25)
            stressActionsAutomated = $true
            externalCpuThresholdPercent = 5
            externalCpuConsecutiveSeconds = 10
        }
        environment = [ordered]@{
            machine = "TEST"
            user = "benchmark"
            windowsCaption = "Windows 11 Pro"
            windowsVersion = "10.0.26200"
            windowsBuild = "26200"
            cpu = "Synthetic CPU"
            logicalProcessors = 8
            memoryBytes = 16GB
            powerPlan = "Balanced"
            powerSource = "AC"
            displays = @([ordered]@{ deviceName = "DISPLAY1"; primary = $true; width = 1920; height = 1080; dpi = 96 })
            gitCommit = "0123456789012345678901234567890123456789"
            gitDirty = $false
        }
        orchestration = [ordered]@{
            mode = "Automated"
            version = 2
            shellLifecycleManaged = $Scenario -eq "TenchyShell"
            idleToolManaged = $Phase -eq "Idle"
            liveTelemetryUnchanged = $true
            releaseGitCommit = "0123456789012345678901234567890123456789"
            completed = $true
            errors = @()
        }
        processPolicy = [ordered]@{
            mainProcessId = 10
            mainProcessStartedAt = "2026-01-01T00:00:00.0000000Z"
            expectedCoreNames = if ($Scenario -eq "Explorer") { @("explorer") } else { @("tenchyshell", "minimalshell") }
            sessionId = 1
        }
        observer = [ordered]@{
            processId = 99
            sessionId = 1
            processName = "pwsh"
            collector = "PowerShell/Get-CimInstance Win32_Process"
            excludedFromAggregates = $true
        }
        runs = $runs
    }
}

function Write-Capture([string]$Directory, [string]$Name, $Capture) {
    if (-not (Test-Path -LiteralPath $Directory)) { New-Item -ItemType Directory -Path $Directory | Out-Null }
    $path = Join-Path $Directory "$Name.json"
    $Capture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

function Assert-Throws([scriptblock]$Action, [string]$ExpectedText) {
    try {
        & $Action
        throw "Se esperaba un error que contuviera '$ExpectedText'."
    } catch {
        if ($_.Exception.Message.IndexOf($ExpectedText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Error inesperado. Se esperaba '$ExpectedText' y se obtuvo: $($_.Exception.Message)"
        }
    }
}

try {
    $validDirectory = Join-Path $testRoot "valid"
    $matrix = @(
        @("Explorer", "Idle"),
        @("TenchyShell", "Idle"),
        @("Explorer", "CommonWorkflow"),
        @("TenchyShell", "CommonWorkflow"),
        @("TenchyShell", "TenchyShellStress")
    )
    foreach ($entry in $matrix) {
        [void](Write-Capture $validDirectory "$($entry[0])-$($entry[1])" (New-Capture $entry[0] $entry[1]))
    }
    $summaryPath = Join-Path $testRoot "summary.md"
    & $summarizer -InputPath $validDirectory -OutputPath $summaryPath
    $summary = Get-Content -Raw -LiteralPath $summaryPath
    if ($summary -notmatch "Deltas TenchyShell" -or $summary -notmatch "Resultados por repeticion" -or
        $summary -notmatch "Observador PowerShell/CIM" -or $summary -notmatch "ShellSession" -or $summary -notmatch "N/D") {
        throw "El resumen sintetico no contiene Core/ShellSession, observador, deltas, repeticiones y manejo del denominador cero."
    }

    $invalidDirectory = Join-Path $testRoot "invalid"
    $schema2 = New-Capture "TenchyShell" "Idle" $false
    $schema2.schemaVersion = 2
    $schema2Path = Write-Capture $invalidDirectory "schema2" $schema2
    Assert-Throws { & $summarizer -InputPath $schema2Path -AllowSmokeTest } "esquema 3"

    foreach ($coreCount in @(0, 2)) {
        $invalidCore = New-Capture "TenchyShell" "Idle" $false
        $invalidCore.runs[0].samples[0] = New-Sample "TenchyShell" 1 1 $coreCount
        $path = Write-Capture $invalidDirectory "core-$coreCount" $invalidCore
        Assert-Throws { & $summarizer -InputPath $path -AllowSmokeTest } "PID principal"
    }

    $wrongSum = New-Capture "TenchyShell" "Idle" $false
    $wrongSum.runs[0].samples[0].totalsByRole.ShellSession.privateBytes++
    $wrongSumPath = Write-Capture $invalidDirectory "wrong-sum" $wrongSum
    Assert-Throws { & $summarizer -InputPath $wrongSumPath -AllowSmokeTest } "suma exacta"

    $observerIncluded = New-Capture "TenchyShell" "Idle" $false
    $observerIncluded.runs[0].samples[0].processes += New-ProcessRow 99 1 "pwsh" "Tool" 0.2 40MB
    $observerIncludedPath = Write-Capture $invalidDirectory "observer-included" $observerIncluded
    Assert-Throws { & $summarizer -InputPath $observerIncludedPath -AllowSmokeTest } "observador"

    $incomplete = New-Capture "TenchyShell" "Idle" $false
    $incomplete.runs[0].samples = @($incomplete.runs[0].samples[0])
    $incompletePath = Write-Capture $invalidDirectory "incomplete" $incomplete
    Assert-Throws { & $summarizer -InputPath $incompletePath -AllowSmokeTest } "repeticion incompleta"

    $contaminated = New-Capture "TenchyShell" "Idle" $false
    $contaminated.runs[0].samples[1].contaminationDetected = $true
    $contaminatedPath = Write-Capture $invalidDirectory "contaminated" $contaminated
    Assert-Throws { & $summarizer -InputPath $contaminatedPath -AllowSmokeTest } "carga externa sostenida"

    $environmentA = New-Capture "TenchyShell" "Idle" $false
    $environmentB = New-Capture "Explorer" "Idle" $false
    $environmentB.environment.memoryBytes = 32GB
    $mixedDirectory = Join-Path $testRoot "mixed-environment"
    [void](Write-Capture $mixedDirectory "a" $environmentA)
    [void](Write-Capture $mixedDirectory "b" $environmentB)
    Assert-Throws { & $summarizer -InputPath $mixedDirectory -AllowSmokeTest } "entorno diferente"

    $settingsA = New-Capture "TenchyShell" "Idle" $false
    $settingsB = New-Capture "Explorer" "Idle" $false
    $settingsB.settings.intervalMilliseconds = 2000
    $mixedSettingsDirectory = Join-Path $testRoot "mixed-settings"
    [void](Write-Capture $mixedSettingsDirectory "a" $settingsA)
    [void](Write-Capture $mixedSettingsDirectory "b" $settingsB)
    Assert-Throws { & $summarizer -InputPath $mixedSettingsDirectory -AllowSmokeTest } "ajustes de medicion diferentes"

    $manualActions = New-Capture "TenchyShell" "TenchyShellStress" $false
    $automatedActions = New-Capture "TenchyShell" "Idle" $false
    $manualActions.settings.stressActionsAutomated = $false
    $mixedActionsDirectory = Join-Path $testRoot "mixed-actions"
    [void](Write-Capture $mixedActionsDirectory "manual" $manualActions)
    [void](Write-Capture $mixedActionsDirectory "automated" $automatedActions)
    Assert-Throws { & $summarizer -InputPath $mixedActionsDirectory -AllowSmokeTest } "ajustes de medicion diferentes"

    $shortOfficial = New-Capture "TenchyShell" "Idle" $true
    $shortOfficial.settings.repetitions = 1
    $shortOfficial.runs = @($shortOfficial.runs[0])
    $shortPath = Write-Capture $invalidDirectory "short-official" $shortOfficial
    Assert-Throws { & $summarizer -InputPath $shortPath } "menos de cinco"

    $manualOfficial = New-Capture "TenchyShell" "Idle" $true
    $manualOfficial.orchestration = $null
    $manualOfficialPath = Write-Capture $invalidDirectory "manual-official" $manualOfficial
    Assert-Throws { & $summarizer -InputPath $manualOfficialPath } "orquestador automatizado"

    $wrongRelease = New-Capture "TenchyShell" "Idle" $true
    $wrongRelease.orchestration.releaseGitCommit = "different"
    $wrongReleasePath = Write-Capture $invalidDirectory "wrong-release" $wrongRelease
    Assert-Throws { & $summarizer -InputPath $wrongReleasePath } "publicación Release"

    $manualStressOfficial = New-Capture "TenchyShell" "TenchyShellStress" $true
    $manualStressOfficial.settings.stressActionsAutomated = $false
    $manualStressPath = Write-Capture $invalidDirectory "manual-stress-official" $manualStressOfficial
    Assert-Throws { & $summarizer -InputPath $manualStressPath } "acciones de estrés automatizadas"

    $smokePlan = @(& $orchestrator -SmokeTest -PlanOnly)
    if ($smokePlan.Count -ne 1 -or $smokePlan[0].Scenario -ne "TenchyShell" -or $smokePlan[0].Phase -ne "Idle") {
        throw "El plan automatizado de smoke tests debe contener únicamente TenchyShell/Idle."
    }
    $orchestratorText = Get-Content -Raw -LiteralPath $orchestrator
    if ($orchestratorText -match "Stop-SmokeOwnedProcesses" -or $orchestratorText -notmatch "cleanupCompleted") {
        throw "El orquestador debe conservar el diagnóstico de limpieza y no forzar el workload desde SmokeTest."
    }
    $officialPlan = @(& $orchestrator -BatchId ("synthetic-" + [Guid]::NewGuid().ToString("N")) -PlanOnly)
    if ($officialPlan.Count -ne 1 -or $officialPlan[0].Scenario -ne "TenchyShell" -or $officialPlan[0].Phase -ne "Idle") {
        throw "El plan oficial automatizado no comienza con TenchyShell/Idle."
    }
    Assert-Throws { & $orchestrator -BatchId "invalid batch" -PlanOnly } "BatchId"

    $legacy = New-Capture "TenchyShell" "Idle" $false
    $legacy.schemaVersion = 2
    $legacy.orchestration.version = 1
    foreach ($run in $legacy.runs) {
        foreach ($sample in $run.samples) {
            $legacyProcesses = @($sample.processes | Where-Object { $_.role -in @("Core", "Tool") })
            foreach ($process in $legacyProcesses) {
                if ($process.role -eq "Core") { $process.role = "Shell" }
            }
            $sample.processes = $legacyProcesses
            $sample["shellProcessCount"] = 1
            $sample.totalsByRole = [ordered]@{
                Shell = Get-SyntheticTotals @($legacyProcesses | Where-Object role -eq "Shell")
                Tool = Get-SyntheticTotals @($legacyProcesses | Where-Object role -eq "Tool")
                Total = Get-SyntheticTotals $legacyProcesses
            }
        }
    }
    $legacyPath = Write-Capture $testRoot "legacy-v2" $legacy
    $legacySummary = @(& $summarizer -InputPath $legacyPath -AllowSmokeTest -SchemaVersion 2) -join [Environment]::NewLine
    if ($legacySummary -notmatch "esquema 2") { throw "El resumidor no conservó la lectura separada de lotes v2." }
    Assert-Throws { & $summarizer -InputPath @($legacyPath, $schema2Path) -AllowSmokeTest } "esquema 2"

    Write-Host "Instrumental de rendimiento: pruebas sinteticas y negativas superadas."
} finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTest -like "*TenchyShell.Performance.Tests.*" -and
        [IO.Directory]::Exists($resolvedTest)) {
        [IO.Directory]::Delete($resolvedTest, $true)
    }
}
