[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TenchyShellPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigurationPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [ValidateCount(2, 2)]
    [string[]]$AppUserModelId,

    [ValidateRange(20, 60)]
    [int]$MaximumTraceSeconds = 45,

    [switch]$AcknowledgeIsolatedSession
)

<##
.SYNOPSIS
Captura la comparación ETW de activación MSIX con Explorer ausente y presente.

.DESCRIPTION
Solo para una VM o cuenta secundaria que ya comienza sin explorer.exe. El script
no modifica Winlogon, registro, servicios ni paquetes. Crea un perfil WPRP en
TEMP, lo elimina al terminar y deja los ETL y metadatos locales en
%LOCALAPPDATA%\TenchyShell\diagnostics.

El script rechaza una sesión que ya tenga Shell_TrayWnd o un explorer.exe para
no dejar sin shell a una sesión normal. Después de la primera captura inicia
explorer.exe de forma explícita para la comparación y, incluso si falla la
segunda captura, solicita su salida cooperativa mediante TenchyShell.
##>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ShellTrayHandle {
    if (-not ('TenchyShell.Diagnostics.NativeWindow' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace TenchyShell.Diagnostics
{
    public static class NativeWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);
    }
}
'@
    }

    return [TenchyShell.Diagnostics.NativeWindow]::FindWindow('Shell_TrayWnd', $null)
}

function Test-ShellTrayPresent {
    return (Get-ShellTrayHandle) -ne [IntPtr]::Zero
}

function Get-SessionExplorerProcess {
    $sessionId = (Get-Process -Id $PID).SessionId
    return @(Get-Process -Name explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $sessionId } |
        Select-Object Id, ProcessName, SessionId)
}

function Wait-ForShellTray {
    param(
        [Parameter(Mandatory)] [bool]$ExpectedPresent,
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    do {
        if ((Test-ShellTrayPresent) -eq $ExpectedPresent) {
            return $true
        }

        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::Now -lt $deadline)

    return $false
}

function Get-WprPath {
    $command = Get-Command wpr.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Windows Performance Toolkit\wpr.exe'
    if (Test-Path -LiteralPath $kitPath -PathType Leaf) {
        return $kitPath
    }

    throw 'No se encontró wpr.exe. Instala Windows Performance Toolkit antes de ejecutar esta prueba.'
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-TemporaryWprProfile {
    $directory = Join-Path ([IO.Path]::GetTempPath()) 'TenchyShell'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $profilePath = Join-Path $directory ("packaged-activation-{0:N}.wprp" -f [Guid]::NewGuid())

    # La máscara completa de COMRuntime incluye los keywords de manifiesto
    # ComCallWaitAnalysis y Activations; WPR no acepta sus nombres como filtros.
    $profile = @'
<?xml version="1.0" encoding="utf-8"?>
<WindowsPerformanceRecorder Version="1.0" Author="TenchyShell" Company="TenchyShell">
  <Profiles>
    <SystemCollector Id="TenchyShellKernelCollector" Name="NT Kernel Logger">
      <BufferSize Value="128" />
      <Buffers Value="64" />
    </SystemCollector>
    <EventCollector Id="TenchyShellComRpcCollector" Name="TenchyShell COM RPC Collector">
      <BufferSize Value="128" />
      <Buffers Value="64" />
    </EventCollector>
    <SystemProvider Id="TenchyShellProcessProvider">
      <Keywords>
        <Keyword Value="ProcessThread" />
      </Keywords>
    </SystemProvider>
    <EventProvider Id="TenchyShellComRuntimeProvider" Name="Microsoft-Windows-COMRuntime">
      <Keywords>
        <Keyword Value="0xFFFFFFFFFFFFFFFF" />
      </Keywords>
    </EventProvider>
    <EventProvider Id="TenchyShellRpcProvider" Name="Microsoft-Windows-RPC" Level="5" />
    <EventProvider Id="TenchyShellDistributedComProvider" Name="Microsoft-Windows-DistributedCOM" Level="2" />
    <Profile Id="TenchyShell.PackagedActivation.Verbose.Memory" Name="TenchyShell.PackagedActivation"
             Description="COM/RPC activation dependency comparison" LoggingMode="Memory" DetailLevel="Verbose">
      <Collectors>
        <SystemCollectorId Value="TenchyShellKernelCollector">
          <SystemProviderId Value="TenchyShellProcessProvider" />
        </SystemCollectorId>
        <EventCollectorId Value="TenchyShellComRpcCollector">
          <EventProviders>
            <EventProviderId Value="TenchyShellComRuntimeProvider" />
            <EventProviderId Value="TenchyShellRpcProvider" />
            <EventProviderId Value="TenchyShellDistributedComProvider" />
          </EventProviders>
        </EventCollectorId>
      </Collectors>
    </Profile>
  </Profiles>
</WindowsPerformanceRecorder>
'@

    Set-Content -LiteralPath $profilePath -Value $profile -Encoding utf8
    return $profilePath
}

function Invoke-Wpr {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    $output = & $script:WprPath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Out-String).Trim()
        throw "WPR falló (código $LASTEXITCODE): $detail"
    }
}

function Invoke-TenchyShellProcess {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$ConsoleLogPath
    )

    if ($script:TenchyShellPath.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        $output = & dotnet $script:TenchyShellPath @Arguments 2>&1
    }
    else {
        $output = & $script:TenchyShellPath @Arguments 2>&1
    }

    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $ConsoleLogPath -Encoding utf8
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "TenchyShell terminó con código $exitCode. Consulta '$ConsoleLogPath'."
    }
}

function Invoke-DiagnosticCapture {
    param(
        [Parameter(Mandatory)] [string]$Scenario,
        [Parameter(Mandatory)] [bool]$ExplorerExpected
    )

    $trayBefore = Test-ShellTrayPresent
    if ($trayBefore -ne $ExplorerExpected) {
        throw "El escenario '$Scenario' esperaba Shell_TrayWnd=$ExplorerExpected antes de la captura, pero se observó $trayBefore."
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $etlPath = Join-Path $script:DiagnosticsDirectory ("msix-activation-{0}-{1}.etl" -f $Scenario, $stamp)
    $consoleLogPath = Join-Path $script:DiagnosticsDirectory ("msix-activation-{0}-{1}.console.log" -f $Scenario, $stamp)
    $profilePath = New-TemporaryWprProfile
    $recording = $false
    $startedAt = [DateTimeOffset]::Now

    try {
        Invoke-Wpr -Arguments @('-start', $profilePath)
        $recording = $true

        $arguments = @()
        if (-not $ExplorerExpected) {
            $arguments += '--without-explorer'
        }

        foreach ($aumid in $script:AppUserModelIds) {
            $arguments += '--diagnose-packaged', $aumid
        }

        $arguments += '--activation-context', 'both', '--exit-after-seconds', $script:MaximumTraceSeconds,
            '--without-tui', $script:ConfigurationPath
        Invoke-TenchyShellProcess -Arguments $arguments -ConsoleLogPath $consoleLogPath
    }
    finally {
        if ($recording) {
            Invoke-Wpr -Arguments @('-stop', $etlPath, '-skipPdbGen')
        }

        Remove-Item -LiteralPath $profilePath -Force -ErrorAction SilentlyContinue
    }

    $trayAfter = Test-ShellTrayPresent
    if ($trayAfter -ne $ExplorerExpected) {
        throw "El escenario '$Scenario' terminó con Shell_TrayWnd=$trayAfter; se esperaba $ExplorerExpected."
    }

    return [pscustomobject]@{
        Scenario = $Scenario
        ExplorerShellTrayBefore = $trayBefore
        ExplorerShellTrayAfter = $trayAfter
        StartedAt = $startedAt
        FinishedAt = [DateTimeOffset]::Now
        EtlPath = $etlPath
        ConsoleLogPath = $consoleLogPath
    }
}

function Stop-ExplorerCooperatively {
    $consoleLogPath = Join-Path $script:DiagnosticsDirectory ("msix-activation-cleanup-{0}.console.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Invoke-TenchyShellProcess -Arguments @(
        '--without-explorer',
        '--exit-after-seconds', '15',
        '--without-tui',
        $script:ConfigurationPath
    ) -ConsoleLogPath $consoleLogPath

    if (-not (Wait-ForShellTray -ExpectedPresent $false)) {
        throw "La limpieza de TenchyShell no logró retirar Shell_TrayWnd. Consulta '$consoleLogPath'."
    }

    return $consoleLogPath
}

if (-not $AcknowledgeIsolatedSession) {
    throw 'Confirma una VM o usuario secundario sin Explorer mediante -AcknowledgeIsolatedSession.'
}

if (-not (Test-Administrator)) {
    throw 'Este perfil necesita una consola PowerShell elevada para el recolector ETW de procesos. Reinicia PowerShell como administrador en la VM o usuario secundario y vuelve a ejecutar el script.'
}

$script:TenchyShellPath = (Resolve-Path -LiteralPath $TenchyShellPath -ErrorAction Stop).Path
$script:ConfigurationPath = (Resolve-Path -LiteralPath $ConfigurationPath -ErrorAction Stop).Path
$script:AppUserModelIds = @($AppUserModelId)
$script:MaximumTraceSeconds = $MaximumTraceSeconds
$script:WprPath = Get-WprPath
$script:DiagnosticsDirectory = Join-Path $env:LOCALAPPDATA 'TenchyShell\diagnostics'
[IO.Directory]::CreateDirectory($script:DiagnosticsDirectory) | Out-Null

if (Test-ShellTrayPresent) {
    throw 'Esta sesión ya tiene Shell_TrayWnd. El script solo acepta una VM o usuario secundario que comience sin Explorer.'
}

$existingExplorers = @(Get-SessionExplorerProcess)
if ($existingExplorers.Count -ne 0) {
    $ids = $existingExplorers.Id -join ', '
    throw "Esta sesión tiene explorer.exe residual (PID: $ids). No se arriesga a iniciar una segunda instancia; limpia la sesión de prueba primero."
}

if (-not $PSCmdlet.ShouldProcess('la sesión aislada actual', 'capturar ETW, iniciar Explorer para la comparación y detenerlo cooperativamente al finalizar')) {
    return
}

$captures = @()
$explorerStartedByScript = $false
$cleanupLogPath = $null
try {
    $captures += Invoke-DiagnosticCapture -Scenario 'without-explorer' -ExplorerExpected $false

    Start-Process -FilePath 'explorer.exe' | Out-Null
    $explorerStartedByScript = $true
    if (-not (Wait-ForShellTray -ExpectedPresent $true)) {
        throw 'Explorer no creó Shell_TrayWnd dentro de 15 segundos; no se ejecutará el escenario con Explorer.'
    }

    $captures += Invoke-DiagnosticCapture -Scenario 'with-explorer' -ExplorerExpected $true
}
finally {
    if ($explorerStartedByScript) {
        $cleanupLogPath = Stop-ExplorerCooperatively
    }
}

$summary = [pscustomobject]@{
    CapturedAt = [DateTimeOffset]::Now
    AppUserModelIds = $script:AppUserModelIds
    MaximumTraceSeconds = $script:MaximumTraceSeconds
    ExplorerInitialState = 'Shell_TrayWnd absent and no explorer.exe in current session'
    ExplorerFinalState = if (Test-ShellTrayPresent) { 'Shell_TrayWnd present (unexpected)' } else { 'Shell_TrayWnd absent' }
    Captures = $captures
    CleanupConsoleLogPath = $cleanupLogPath
    Analysis = 'Abrir los ETL en WPA; correlacionar ActivateApplication con COM/RPC, CLSID y PID/imagen del servidor. El script no atribuye la causa automáticamente.'
}
$summaryPath = Join-Path $script:DiagnosticsDirectory ("msix-activation-summary-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8

Write-Host "Captura completada. Resumen: $summaryPath"
foreach ($capture in $captures) {
    Write-Host "- $($capture.Scenario): $($capture.EtlPath)"
}
