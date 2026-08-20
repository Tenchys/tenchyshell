[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$summarizer = Join-Path $PSScriptRoot "summarize-performance.ps1"
$orchestrator = Join-Path $PSScriptRoot "invoke-performance-benchmark.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.Performance.Tests." + [Guid]::NewGuid().ToString("N"))

function New-Sample([string]$Scenario, [double]$ShellCpu, [double]$ToolCpu, [int]$ShellCount = 1) {
    $shellName = if ($Scenario -eq "Explorer") { "explorer" } else { "TenchyShell" }
    $shell = [ordered]@{
        processCount = $ShellCount
        cpuPercent = $ShellCpu
        privateBytes = 100MB
        workingSetBytes = 80MB
        handles = 100
        threads = 10
        readBytesPerSecond = 0
        writeBytesPerSecond = 0
    }
    $tool = [ordered]@{
        processCount = 1
        cpuPercent = $ToolCpu
        privateBytes = 50MB
        workingSetBytes = 40MB
        handles = 50
        threads = 5
        readBytesPerSecond = 1MB
        writeBytesPerSecond = 0.5MB
    }
    $total = [ordered]@{
        processCount = $ShellCount + 1
        cpuPercent = $ShellCpu + $ToolCpu
        privateBytes = 150MB
        workingSetBytes = 120MB
        handles = 150
        threads = 15
        readBytesPerSecond = 1MB
        writeBytesPerSecond = 0.5MB
    }
    return [ordered]@{
        capturedAt = (Get-Date).ToUniversalTime().ToString("O")
        shellProcessCount = $ShellCount
        contaminationDetected = $false
        processes = @(
            [ordered]@{ id = 10; name = $shellName; role = "Shell" },
            [ordered]@{ id = 20; name = "yazi"; role = "Tool" }
        )
        totalsByRole = [ordered]@{ Shell = $shell; Tool = $tool; Total = $total }
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
        schemaVersion = 2
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
            version = 1
            shellLifecycleManaged = $Scenario -eq "TenchyShell"
            idleToolManaged = $Phase -eq "Idle"
            releaseGitCommit = "0123456789012345678901234567890123456789"
            completed = $true
            errors = @()
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
    if ($summary -notmatch "Deltas TenchyShell" -or $summary -notmatch "Resultados por repeticion" -or $summary -notmatch "N/D") {
        throw "El resumen sintetico no contiene deltas, repeticiones y manejo del denominador cero."
    }

    $invalidDirectory = Join-Path $testRoot "invalid"
    $schema1 = New-Capture "TenchyShell" "Idle" $false
    $schema1.schemaVersion = 1
    $schema1Path = Write-Capture $invalidDirectory "schema1" $schema1
    Assert-Throws { & $summarizer -InputPath $schema1Path -AllowSmokeTest } "esquema 2"

    foreach ($shellCount in @(0, 2)) {
        $invalidShell = New-Capture "TenchyShell" "Idle" $false
        $invalidShell.runs[0].samples[0] = New-Sample "TenchyShell" 1 1 $shellCount
        $path = Write-Capture $invalidDirectory "shell-$shellCount" $invalidShell
        Assert-Throws { & $summarizer -InputPath $path -AllowSmokeTest } "exactamente una shell"
    }

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
    if ($smokePlan.Count -ne 3 -or $smokePlan[0].Phase -ne "Idle" -or $smokePlan[2].Phase -ne "TenchyShellStress") {
        throw "El plan automatizado de smoke tests no contiene las tres fases en el orden esperado."
    }
    $officialPlan = @(& $orchestrator -BatchId ("synthetic-" + [Guid]::NewGuid().ToString("N")) -PlanOnly)
    if ($officialPlan.Count -ne 1 -or $officialPlan[0].Scenario -ne "Explorer" -or $officialPlan[0].Phase -ne "Idle") {
        throw "El plan oficial automatizado no comienza con Explorer/Idle."
    }
    Assert-Throws { & $orchestrator -BatchId "invalid batch" -PlanOnly } "BatchId"

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
