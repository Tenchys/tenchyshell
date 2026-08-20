[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$InputPath,

    [string]$OutputPath,

    [switch]$AllowSmokeTest
)

$ErrorActionPreference = "Stop"
$expectedMatrix = @(
    "Explorer/Idle",
    "TenchyShell/Idle",
    "Explorer/CommonWorkflow",
    "TenchyShell/CommonWorkflow",
    "TenchyShell/TenchyShellStress"
)
$roles = @("Shell", "Tool", "Total")
$metrics = @(
    [ordered]@{ Name = "CPU (%)"; Property = "cpuPercent"; Scale = 1.0 },
    [ordered]@{ Name = "Memoria privada (MiB)"; Property = "privateBytes"; Scale = 1MB },
    [ordered]@{ Name = "Working set (MiB)"; Property = "workingSetBytes"; Scale = 1MB },
    [ordered]@{ Name = "Lectura (MiB/s)"; Property = "readBytesPerSecond"; Scale = 1MB },
    [ordered]@{ Name = "Escritura (MiB/s)"; Property = "writeBytesPerSecond"; Scale = 1MB },
    [ordered]@{ Name = "Handles"; Property = "handles"; Scale = 1.0 },
    [ordered]@{ Name = "Hilos"; Property = "threads"; Scale = 1.0 }
)

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

function Get-Summary([double[]]$Values) {
    $finite = @($Values | Where-Object { -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_) })
    if ($finite.Count -eq 0) { return $null }
    $quartile1 = Get-Percentile $finite 0.25
    $quartile3 = Get-Percentile $finite 0.75
    return [ordered]@{
        count = $finite.Count
        minimum = ($finite | Measure-Object -Minimum).Minimum
        maximum = ($finite | Measure-Object -Maximum).Maximum
        median = Get-Percentile $finite 0.5
        percentile95 = Get-Percentile $finite 0.95
        quartile1 = $quartile1
        quartile3 = $quartile3
        interquartileRange = $quartile3 - $quartile1
    }
}

function Get-ScaledValues($Samples, [string]$Role, [string]$Property, [double]$Scale) {
    $values = [Collections.Generic.List[double]]::new()
    foreach ($sample in @($Samples)) {
        $roleTotals = $sample.totalsByRole.$Role
        if ($null -eq $roleTotals) { continue }
        $value = $roleTotals.$Property
        if ($null -ne $value) { $values.Add([double]$value / $Scale) }
    }
    return [double[]]$values.ToArray()
}

function Format-Number($Value) {
    if ($null -eq $Value) { return "N/D" }
    return ([Math]::Round([double]$Value, 2)).ToString("0.##", [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Fingerprint($Value) {
    return ($Value | ConvertTo-Json -Depth 10 -Compress)
}

$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($path in $InputPath) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files.Add((Get-Item -LiteralPath $path))
    } elseif (Test-Path -LiteralPath $path -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $path -Filter "*.json" -File)) { $files.Add($file) }
    } else {
        throw "No existe la entrada: $path"
    }
}
$files = @($files | Sort-Object FullName -Unique)
if ($files.Count -eq 0) { throw "No se encontraron archivos JSON de benchmark." }

$captures = [Collections.Generic.List[object]]::new()
$groups = @{}
$environmentFingerprint = $null
$settingsFingerprint = $null
$commitFingerprint = $null
$batchId = $null

foreach ($file in $files) {
    $data = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
    if ([int]$data.schemaVersion -ne 2) {
        throw "El archivo '$($file.FullName)' no usa exclusivamente el esquema 2."
    }
    if ($data.scenario -notin @("TenchyShell", "Explorer") -or
        $data.phase -notin @("Idle", "CommonWorkflow", "TenchyShellStress")) {
        throw "El archivo '$($file.FullName)' no identifica un escenario y fase validos."
    }
    if ($data.phase -eq "TenchyShellStress" -and $data.scenario -ne "TenchyShell") {
        throw "El archivo '$($file.FullName)' usa TenchyShellStress con un escenario no valido."
    }
    if (-not [bool]$data.valid -or @($data.runs | Where-Object { -not [bool]$_.valid }).Count -gt 0) {
        throw "El archivo '$($file.FullName)' esta marcado como invalido y no puede resumirse."
    }
    if ([int]$data.settings.repetitions -ne @($data.runs).Count) {
        throw "El archivo '$($file.FullName)' esta incompleto: no coincide el numero de repeticiones."
    }
    if ([bool]$data.official) {
        if ([int]$data.settings.repetitions -lt 5) {
            throw "El archivo oficial '$($file.FullName)' contiene menos de cinco repeticiones."
        }
        if ([string]::IsNullOrWhiteSpace([string]$data.environment.gitCommit) -or [bool]$data.environment.gitDirty) {
            throw "El archivo oficial '$($file.FullName)' debe proceder de un commit identificable y un arbol limpio."
        }
        if ([string]::IsNullOrWhiteSpace([string]$data.batchId)) {
            throw "El archivo oficial '$($file.FullName)' no contiene batchId."
        }
        if ($null -eq $data.orchestration -or [string]$data.orchestration.mode -ne "Automated" -or
            [int]$data.orchestration.version -ne 1 -or -not [bool]$data.orchestration.completed) {
            throw "El archivo oficial '$($file.FullName)' no fue completado por el orquestador automatizado compatible."
        }
        if ([string]$data.orchestration.releaseGitCommit -ne [string]$data.environment.gitCommit) {
            throw "El archivo oficial '$($file.FullName)' no corresponde a la publicación Release del commit medido."
        }
        if (-not [bool]$data.settings.stressActionsAutomated) {
            throw "El archivo oficial '$($file.FullName)' no declara acciones de estrés automatizadas."
        }
    } elseif (-not $AllowSmokeTest) {
        throw "El archivo '$($file.FullName)' es un smoke test. Usa -AllowSmokeTest para analizarlo fuera del informe oficial."
    } elseif ([int]$data.settings.repetitions -ne 1) {
        throw "El smoke test '$($file.FullName)' debe contener exactamente una repeticion."
    }
    if ([string]::IsNullOrWhiteSpace([string]$data.environment.windowsVersion) -or
        [int]$data.environment.logicalProcessors -le 0 -or
        [long]$data.environment.memoryBytes -le 0 -or
        $null -eq $data.environment.displays) {
        throw "El archivo '$($file.FullName)' no contiene metadatos suficientes del entorno."
    }

    foreach ($run in @($data.runs)) {
        if ([int]$run.repetition -le 0 -or @($run.samples).Count -ne [int]$data.settings.samplesPerRepetition) {
            throw "El archivo '$($file.FullName)' contiene una repeticion incompleta."
        }
        foreach ($sample in @($run.samples)) {
            if ([int]$sample.shellProcessCount -ne 1) {
                throw "El archivo '$($file.FullName)' no contiene exactamente una shell en todas sus muestras."
            }
            if ([bool]$sample.contaminationDetected) {
                throw "El archivo '$($file.FullName)' contiene carga externa sostenida y debe descartarse."
            }
            if ($null -eq $sample.totalsByRole.Shell -or $null -eq $sample.totalsByRole.Tool -or $null -eq $sample.totalsByRole.Total) {
                throw "El archivo '$($file.FullName)' no contiene totales para Shell, Tool y Total."
            }
            if (@($sample.processes | Where-Object { $_.role -notin @("Shell", "Tool") }).Count -gt 0) {
                throw "El archivo '$($file.FullName)' contiene procesos sin un rol valido."
            }
        }
    }

    $currentEnvironment = Get-Fingerprint ([ordered]@{
        machine = $data.environment.machine
        user = $data.environment.user
        windowsVersion = $data.environment.windowsVersion
        windowsBuild = $data.environment.windowsBuild
        cpu = $data.environment.cpu
        logicalProcessors = $data.environment.logicalProcessors
        memoryBytes = $data.environment.memoryBytes
        powerPlan = $data.environment.powerPlan
        powerSource = $data.environment.powerSource
        displays = $data.environment.displays
    })
    $currentSettings = Get-Fingerprint ([ordered]@{
        samplesPerRepetition = $data.settings.samplesPerRepetition
        intervalMilliseconds = $data.settings.intervalMilliseconds
        warmupSeconds = $data.settings.warmupSeconds
        interRepetitionSeconds = $data.settings.interRepetitionSeconds
        workflowLaunchSecond = $data.settings.workflowLaunchSecond
        workflowCloseSecond = $data.settings.workflowCloseSecond
        workflowVerifyClosedSecond = $data.settings.workflowVerifyClosedSecond
        stressActionSeconds = $data.settings.stressActionSeconds
        stressActionsAutomated = $data.settings.stressActionsAutomated
        externalCpuThresholdPercent = $data.settings.externalCpuThresholdPercent
        externalCpuConsecutiveSeconds = $data.settings.externalCpuConsecutiveSeconds
    })
    $currentCommit = [string]$data.environment.gitCommit
    if ($null -eq $environmentFingerprint) { $environmentFingerprint = $currentEnvironment }
    elseif ($environmentFingerprint -ne $currentEnvironment) { throw "El archivo '$($file.FullName)' pertenece a un entorno diferente." }
    if ($null -eq $settingsFingerprint) { $settingsFingerprint = $currentSettings }
    elseif ($settingsFingerprint -ne $currentSettings) { throw "El archivo '$($file.FullName)' usa ajustes de medicion diferentes." }
    if ($null -eq $commitFingerprint) { $commitFingerprint = $currentCommit }
    elseif ($commitFingerprint -ne $currentCommit) { throw "El archivo '$($file.FullName)' procede de un commit diferente." }
    if ($null -eq $batchId) { $batchId = [string]$data.batchId }
    elseif ($batchId -ne [string]$data.batchId) { throw "El archivo '$($file.FullName)' pertenece a un batchId diferente." }

    $key = "$($data.scenario)/$($data.phase)"
    if ($groups.ContainsKey($key)) { throw "Hay mas de una captura para '$key'; selecciona exactamente un archivo por grupo." }
    $groups[$key] = $data
    $captures.Add([pscustomobject]@{ File = $file; Data = $data; Key = $key })
}

if (-not $AllowSmokeTest) {
    $missing = @($expectedMatrix | Where-Object { -not $groups.ContainsKey($_) })
    $extra = @($groups.Keys | Where-Object { $_ -notin $expectedMatrix })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "La matriz oficial no esta completa. Faltan: $($missing -join ', '). Grupos inesperados: $($extra -join ', ')."
    }
}

$aggregateRows = [Collections.Generic.List[object]]::new()
$repetitionRows = [Collections.Generic.List[object]]::new()
$stabilityRows = [Collections.Generic.List[object]]::new()
foreach ($capture in $captures) {
    $data = $capture.Data
    foreach ($role in $roles) {
        foreach ($metric in $metrics) {
            $runMedians = [Collections.Generic.List[double]]::new()
            $allValues = [Collections.Generic.List[double]]::new()
            foreach ($run in @($data.runs)) {
                [double[]]$values = Get-ScaledValues $run.samples $role $metric.Property ([double]$metric.Scale)
                if ($values.Count -eq 0) { continue }
                $runSummary = Get-Summary $values
                $runMedians.Add([double]$runSummary.median)
                foreach ($value in $values) { $allValues.Add($value) }
                $repetitionRows.Add([pscustomobject]@{
                    Scenario = $data.scenario
                    Phase = $data.phase
                    Role = $role
                    Repetition = $run.repetition
                    Metric = $metric.Name
                    Median = $runSummary.median
                })
            }
            if ($runMedians.Count -eq 0) { continue }
            $runAggregate = Get-Summary ([double[]]$runMedians.ToArray())
            $sampleAggregate = Get-Summary ([double[]]$allValues.ToArray())
            $aggregateRows.Add([pscustomobject]@{
                Scenario = $data.scenario
                Phase = $data.phase
                Role = $role
                Metric = $metric.Name
                Property = $metric.Property
                Runs = $runMedians.Count
                Samples = $allValues.Count
                MinimumRunMedian = $runAggregate.minimum
                MedianOfRuns = $runAggregate.median
                IqrOfRuns = $runAggregate.interquartileRange
                P95Samples = $sampleAggregate.percentile95
                MaximumRunMedian = $runAggregate.maximum
            })
        }
    }

    foreach ($run in @($data.runs)) {
        $first = @($run.samples)[0].totalsByRole.Shell
        $last = @($run.samples)[-1].totalsByRole.Shell
        $stabilityRows.Add([pscustomobject]@{
            Scenario = $data.scenario
            Phase = $data.phase
            Repetition = $run.repetition
            PrivateMemoryDeltaMiB = ([double]$last.privateBytes - [double]$first.privateBytes) / 1MB
            HandlesDelta = [double]$last.handles - [double]$first.handles
            ThreadsDelta = [double]$last.threads - [double]$first.threads
        })
    }
}

$report = [Collections.Generic.List[string]]::new()
$report.Add("# Resumen de rendimiento TenchyShell - esquema 2")
$report.Add("")
$report.Add("Generado: $((Get-Date).ToUniversalTime().ToString('O'))")
$report.Add("")
$report.Add("- Lote: $(if ([string]::IsNullOrWhiteSpace($batchId)) { 'smoke test' } else { $batchId })")
$report.Add("- Commit: $(if ([string]::IsNullOrWhiteSpace($commitFingerprint)) { 'no disponible' } else { $commitFingerprint })")
$report.Add("- Entorno: $($captures[0].Data.environment.windowsCaption) $($captures[0].Data.environment.windowsVersion), $($captures[0].Data.environment.cpu), $([Math]::Round([double]$captures[0].Data.environment.memoryBytes / 1GB, 1)) GiB")
$report.Add("")
$report.Add("## Agregados")
$report.Add("")
$report.Add("La cifra principal es la mediana de las medianas de las repeticiones. El IQR mide su dispersion y el P95 se calcula sobre muestras no nulas; el primer intervalo sin delta no se convierte en cero.")
$report.Add("")
$report.Add("| Escenario | Fase | Rol | Metrica | Runs | Muestras | Min. mediana | Mediana | IQR | P95 muestras | Max. mediana |")
$report.Add("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
foreach ($row in @($aggregateRows | Sort-Object Scenario, Phase, Role, Metric)) {
    $report.Add("| $($row.Scenario) | $($row.Phase) | $($row.Role) | $($row.Metric) | $($row.Runs) | $($row.Samples) | $(Format-Number $row.MinimumRunMedian) | $(Format-Number $row.MedianOfRuns) | $(Format-Number $row.IqrOfRuns) | $(Format-Number $row.P95Samples) | $(Format-Number $row.MaximumRunMedian) |")
}

$report.Add("")
$report.Add("## Deltas TenchyShell frente a Explorer")
$report.Add("")
$report.Add("Un delta positivo significa que TenchyShell registro un valor mayor. Cuando Explorer vale cero, el porcentaje se muestra como N/D.")
$report.Add("")
$report.Add("| Fase | Rol | Metrica | Explorer | TenchyShell | Delta absoluto | Delta % |")
$report.Add("|---|---|---|---:|---:|---:|---:|")
foreach ($phase in @("Idle", "CommonWorkflow")) {
    foreach ($role in $roles) {
        foreach ($metric in $metrics) {
            $explorer = $aggregateRows | Where-Object { $_.Scenario -eq "Explorer" -and $_.Phase -eq $phase -and $_.Role -eq $role -and $_.Metric -eq $metric.Name } | Select-Object -First 1
            $tenchy = $aggregateRows | Where-Object { $_.Scenario -eq "TenchyShell" -and $_.Phase -eq $phase -and $_.Role -eq $role -and $_.Metric -eq $metric.Name } | Select-Object -First 1
            if ($null -eq $explorer -or $null -eq $tenchy) { continue }
            $absolute = [double]$tenchy.MedianOfRuns - [double]$explorer.MedianOfRuns
            $percentage = if ([double]$explorer.MedianOfRuns -eq 0) { $null } else { $absolute / [double]$explorer.MedianOfRuns * 100 }
            $report.Add("| $phase | $role | $($metric.Name) | $(Format-Number $explorer.MedianOfRuns) | $(Format-Number $tenchy.MedianOfRuns) | $(Format-Number $absolute) | $(Format-Number $percentage) |")
        }
    }
}

$report.Add("")
$report.Add("## Resultados por repeticion")
$report.Add("")
$report.Add("| Escenario | Fase | Rol | Repeticion | Metrica | Mediana |")
$report.Add("|---|---|---|---:|---|---:|")
foreach ($row in @($repetitionRows | Sort-Object Scenario, Phase, Role, Repetition, Metric)) {
    $report.Add("| $($row.Scenario) | $($row.Phase) | $($row.Role) | $($row.Repetition) | $($row.Metric) | $(Format-Number $row.Median) |")
}

$report.Add("")
$report.Add("## Estabilidad de la shell")
$report.Add("")
$report.Add("Los siguientes deltas comparan la ultima y la primera muestra de cada repeticion. Una tendencia positiva repetida requiere investigacion, pero no se oculta ni invalida automaticamente.")
$report.Add("")
$report.Add("| Escenario | Fase | Repeticion | Memoria privada delta MiB | Handles delta | Hilos delta |")
$report.Add("|---|---|---:|---:|---:|---:|")
foreach ($row in @($stabilityRows | Sort-Object Scenario, Phase, Repetition)) {
    $report.Add("| $($row.Scenario) | $($row.Phase) | $($row.Repetition) | $(Format-Number $row.PrivateMemoryDeltaMiB) | $(Format-Number $row.HandlesDelta) | $(Format-Number $row.ThreadsDelta) |")
}

$report.Add("")
$report.Add("## Limitaciones y criterio")
$report.Add("")
$report.Add("- Esta es una linea base de una sola maquina; no demuestra superioridad global.")
$report.Add("- El recolector introduce una pequena carga observadora y debe ejecutarse desde una consola distinta de la terminal medida.")
$report.Add("- Explorer representa exclusivamente explorer.exe. Los hosts auxiliares de Windows se registran como externos y no se atribuyen a Explorer.")
$report.Add("- TenchyShell incluye el coste de su proceso .NET; WezTerm/Yazi se presentan por separado y tambien dentro del total.")
$report.Add("- TenchyShellStress no tiene equivalente Explorer y nunca participa en los deltas comparativos.")
$report.Add("- Las cinco capturas fueron preparadas por el orquestador y las acciones de estrés se inyectaron y validaron automáticamente.")
$report.Add("- El lote se acepta como linea base si las capturas son validas, la funcionalidad se conserva y cualquier crecimiento repetido de recursos queda explicado o registrado como seguimiento.")

$content = $report -join [Environment]::NewLine
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $content
} else {
    $directory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $content -Encoding utf8
    Write-Host "Resumen guardado en: $OutputPath"
}
