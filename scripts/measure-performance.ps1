[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("TenchyShell", "Explorer")]
    [string]$Scenario,

    [ValidateSet("Idle", "CommonWorkflow", "TenchyShellStress")]
    [string]$Phase = "Idle",

    [ValidateRange(1, 20)]
    [int]$Repetitions = 5,

    [ValidateRange(2, 600)]
    [int]$SamplesPerRepetition = 30,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 1000,

    [ValidateRange(0, 300)]
    [int]$WarmupSeconds = 10,

    [ValidateRange(0, 300)]
    [int]$InterRepetitionSeconds = 15,

    [ValidateRange(0, 600)]
    [int]$WorkflowLaunchSecond = 5,

    [ValidateRange(1, 600)]
    [int]$WorkflowCloseSecond = 20,

    [ValidateRange(1, 600)]
    [int]$WorkflowVerifyClosedSecond = 25,

    [int[]]$StressActionSeconds = @(5, 10, 15, 20, 25),

    [switch]$ManualStressActions,

    [string]$ToolCommand = "wezterm-gui.exe",

    [string[]]$ToolArguments = @("start", "--always-new-process", "--", "yazi.exe"),

    [ValidateRange(0.1, 100)]
    [double]$ExternalCpuThresholdPercent = 5,

    [ValidateRange(1, 60)]
    [int]$ExternalCpuConsecutiveSeconds = 10,

    [string]$BatchId = "",

    [switch]$SmokeTest,

    [switch]$AllowExistingToolsForSmoke,

    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v2")
)

$ErrorActionPreference = "Stop"
$schemaVersion = 2
$logicalProcessorCount = [Environment]::ProcessorCount
$collectorProcessId = $PID
$collectorSessionId = (Get-Process -Id $PID).SessionId
$browserNames = @("brave", "chrome", "firefox", "msedge", "opera")
$toolNames = @("wezterm", "wezterm-gui", "yazi")
$wezTermNames = @("wezterm", "wezterm-gui")
$scenarioNames = if ($Scenario -eq "TenchyShell") { @("tenchyshell", "minimalshell") } else { @("explorer") }
$contaminationExclusions = @(
    "idle", "dwm", "system", "shellexperiencehost", "startmenuexperiencehost",
    "searchhost", "textinputhost", "runtimebroker", "applicationframehost"
)
$captureDurationSeconds = $SamplesPerRepetition * $IntervalMilliseconds / 1000
$consecutiveSamplesRequired = [Math]::Max(1, [Math]::Ceiling($ExternalCpuConsecutiveSeconds * 1000 / $IntervalMilliseconds))

if ($SmokeTest) {
    if ($Repetitions -ne 1) { throw "-SmokeTest requiere exactamente una repeticion." }
} elseif ($Repetitions -lt 5) {
    throw "Una captura oficial requiere al menos cinco repeticiones. Usa -SmokeTest para una comprobacion no oficial."
}
if ($AllowExistingToolsForSmoke -and -not $SmokeTest) {
    throw "-AllowExistingToolsForSmoke solo puede usarse junto con -SmokeTest."
}
if ($ManualStressActions -and $Phase -ne "TenchyShellStress") {
    throw "-ManualStressActions solo puede usarse con TenchyShellStress."
}
if ($ManualStressActions -and -not $SmokeTest) {
    throw "Las capturas oficiales de TenchyShellStress deben usar acciones automatizadas."
}
if (-not $SmokeTest -and [string]::IsNullOrWhiteSpace($BatchId)) {
    throw "Una captura oficial requiere -BatchId para agrupar exactamente los cinco escenarios."
}
if (-not [string]::IsNullOrWhiteSpace($BatchId) -and $BatchId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
    throw "BatchId solo admite letras ASCII, numeros, punto, guion y guion bajo (maximo 64 caracteres)."
}
if ($Phase -eq "TenchyShellStress" -and $Scenario -ne "TenchyShell") {
    throw "TenchyShellStress solo puede medirse en el escenario TenchyShell."
}
if ($Phase -eq "CommonWorkflow") {
    if (-not ($WorkflowLaunchSecond -lt $WorkflowCloseSecond -and
              $WorkflowCloseSecond -lt $WorkflowVerifyClosedSecond -and
              $WorkflowVerifyClosedSecond -lt $captureDurationSeconds)) {
        throw "Los segundos del CommonWorkflow deben cumplir launch < close < verify < duracion de la repeticion."
    }
}
if ($Phase -eq "TenchyShellStress") {
    if ($StressActionSeconds.Count -ne 5 -or @($StressActionSeconds | Where-Object { $_ -le 0 -or $_ -ge $captureDurationSeconds }).Count -gt 0) {
        throw "TenchyShellStress requiere cinco marcas positivas anteriores al fin de la repeticion."
    }
}

if ($Phase -in @("CommonWorkflow", "TenchyShellStress")) {
    if (-not ("TenchyShellBenchmarkInput" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class TenchyShellBenchmarkInput
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

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

    public static bool TrySendCtrlAlt(byte virtualKey)
    {
        const byte Control = 0x11;
        const byte Alt = 0x12;
        const uint KeyUp = 0x0002;
        keybd_event(Control, 0, 0, UIntPtr.Zero);
        keybd_event(Alt, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
        keybd_event(Alt, 0, KeyUp, UIntPtr.Zero);
        keybd_event(Control, 0, KeyUp, UIntPtr.Zero);
        return true;
    }

    public static bool TrySendEscape()
    {
        const byte Escape = 0x1B;
        const uint KeyUp = 0x0002;
        keybd_event(Escape, 0, 0, UIntPtr.Zero);
        keybd_event(Escape, 0, KeyUp, UIntPtr.Zero);
        return true;
    }

    public static bool WaitForTrayVisibility(bool expectedVisible, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            if (IsTrayVisible() == expectedVisible) return true;
            Thread.Sleep(25);
        }
        while (Environment.TickCount64 < deadline);
        return IsTrayVisible() == expectedVisible;
    }

    private static bool IsTrayVisible()
    {
        var found = false;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            var className = new StringBuilder(128);
            if (GetClassName(window, className, className.Capacity) > 0 &&
                className.ToString().StartsWith("TenchyShell.SystemTray.", StringComparison.Ordinal) &&
                IsWindowVisible(window))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
    }
}

function Wait-ForLogMessage([string]$Path, [long]$Offset, [string]$Expected, [int]$TimeoutMilliseconds = 1000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $stream = $null
            $reader = $null
            try {
                $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                if ($Offset -le $stream.Length) {
                    [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
                    $reader = [IO.StreamReader]::new($stream)
                    if ($reader.ReadToEnd().IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) { return $true }
                }
            } finally {
                if ($null -ne $reader) { $reader.Dispose() }
                elseif ($null -ne $stream) { $stream.Dispose() }
            }
        }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Get-NormalizedProcessName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return "" }
    return [IO.Path]::GetFileNameWithoutExtension($Name).ToLowerInvariant()
}

function Get-GitMetadata {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $safeRepoRoot = $repoRoot.Replace('\', '/')
    $commit = $null
    $dirty = $null
    try {
        $commit = (& git -c "safe.directory=$safeRepoRoot" -C $repoRoot rev-parse HEAD 2>$null)
        $dirty = [bool](& git -c "safe.directory=$safeRepoRoot" -C $repoRoot status --porcelain 2>$null)
    } catch {
        # El benchmark tambien puede ejecutarse desde una publicacion sin Git.
    }
    return [ordered]@{ commit = $commit; dirty = $dirty }
}

function Get-DisplayMetadata {
    try {
        if (-not ("TenchyShellBenchmarkDisplays" -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public sealed class TenchyShellBenchmarkDisplay
{
    public string DeviceName { get; set; }
    public bool Primary { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public uint DpiX { get; set; }
    public uint DpiY { get; set; }
    public string DpiSource { get; set; }
}

public static class TenchyShellBenchmarkDisplays
{
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static TenchyShellBenchmarkDisplay[] Capture()
    {
        var displays = new List<TenchyShellBenchmarkDisplay>();
        MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data)
        {
            var info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitor, ref info)) return true;
            uint dpiX = 96, dpiY = 96;
            var source = "Fallback96";
            try
            {
                if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0) source = "GetDpiForMonitor.Effective";
                else { dpiX = 96; dpiY = 96; }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            displays.Add(new TenchyShellBenchmarkDisplay
            {
                DeviceName = info.DeviceName,
                Primary = (info.Flags & 1) != 0,
                Left = info.Monitor.Left,
                Top = info.Monitor.Top,
                Width = info.Monitor.Right - info.Monitor.Left,
                Height = info.Monitor.Bottom - info.Monitor.Top,
                DpiX = dpiX,
                DpiY = dpiY,
                DpiSource = source
            });
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new InvalidOperationException("EnumDisplayMonitors failed.");
        return displays.ToArray();
    }
}
'@
        }
        return @([TenchyShellBenchmarkDisplays]::Capture() | ForEach-Object {
            [ordered]@{
                deviceName = $_.DeviceName
                primary = $_.Primary
                left = $_.Left
                top = $_.Top
                width = $_.Width
                height = $_.Height
                dpiX = $_.DpiX
                dpiY = $_.DpiY
                dpiSource = $_.DpiSource
            }
        })
    } catch {
        return @([ordered]@{ error = $_.Exception.Message })
    }
}

function Get-EnvironmentMetadata {
    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    $computerSystem = Get-CimInstance Win32_ComputerSystem
    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1
    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    $git = Get-GitMetadata
    $powerPlan = $null
    try { $powerPlan = ((& powercfg.exe /GetActiveScheme 2>$null) | Out-String).Trim() } catch { }
    return [ordered]@{
        machine = $env:COMPUTERNAME
        user = $env:USERNAME
        windowsCaption = $operatingSystem.Caption
        windowsVersion = $operatingSystem.Version
        windowsBuild = $operatingSystem.BuildNumber
        cpu = $processor.Name
        logicalProcessors = $logicalProcessorCount
        memoryBytes = [long]$computerSystem.TotalPhysicalMemory
        powerPlan = $powerPlan
        powerSource = if ($null -eq $battery) { "AC/NoBatteryReported" } elseif ([int]$battery.BatteryStatus -eq 1) { "Battery" } else { "AC" }
        displays = @(Get-DisplayMetadata)
        gitCommit = $git.commit
        gitDirty = $git.dirty
    }
}

function Get-RoleMap($CimProcesses) {
    $roles = @{}
    foreach ($process in $CimProcesses) {
        $name = Get-NormalizedProcessName $process.Name
        $processId = [int]$process.ProcessId
        if ($browserNames -contains $name -or $processId -eq $collectorProcessId) { continue }
        if ($scenarioNames -contains $name) { $roles[$processId] = "Shell" }
        if ($toolNames -contains $name) { $roles[$processId] = "Tool" }
    }

    # Solo los descendientes de WezTerm/Yazi se atribuyen a la carga comun.
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $CimProcesses) {
            $processId = [int]$process.ProcessId
            $parentId = [int]$process.ParentProcessId
            $name = Get-NormalizedProcessName $process.Name
            if ($processId -ne $collectorProcessId -and
                $browserNames -notcontains $name -and
                $roles[$parentId] -eq "Tool" -and
                -not $roles.ContainsKey($processId)) {
                $roles[$processId] = "Tool"
                $changed = $true
            }
        }
    }
    return $roles
}

function Get-NullableSum($Rows, [string]$Property) {
    $values = @($Rows | ForEach-Object { $_.$Property } | Where-Object { $null -ne $_ })
    if ($values.Count -eq 0) { return $null }
    return [double](($values | Measure-Object -Sum).Sum)
}

function Get-Totals($Rows) {
    $items = @($Rows)
    return [ordered]@{
        processCount = $items.Count
        cpuPercent = Get-NullableSum $items "cpuPercent"
        privateBytes = [long](Get-NullableSum $items "privateBytes")
        workingSetBytes = [long](Get-NullableSum $items "workingSetBytes")
        handles = [int](Get-NullableSum $items "handles")
        threads = [int](Get-NullableSum $items "threads")
        readBytesPerSecond = Get-NullableSum $items "readBytesPerSecond"
        writeBytesPerSecond = Get-NullableSum $items "writeBytesPerSecond"
    }
}

function Get-ToolRootProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq $collectorSessionId -and
            $toolNames -contains (Get-NormalizedProcessName $_.ProcessName)
    })
}

function Get-Sample(
    [hashtable]$PreviousCpu,
    [hashtable]$PreviousReadBytes,
    [hashtable]$PreviousWriteBytes,
    [hashtable]$ExternalStreaks,
    [datetime]$PreviousTimestamp) {
    $timestamp = Get-Date
    $cimProcesses = @(Get-CimInstance Win32_Process | Where-Object { [int]$_.SessionId -eq $collectorSessionId })
    $roles = Get-RoleMap $cimProcesses
    $shellRoots = @($cimProcesses | Where-Object { $scenarioNames -contains (Get-NormalizedProcessName $_.Name) })
    $elapsedMilliseconds = [Math]::Max(1, ($timestamp - $PreviousTimestamp).TotalMilliseconds)
    $processRows = [Collections.Generic.List[object]]::new()
    $externalRows = [Collections.Generic.List[object]]::new()
    $activeExternalIds = [Collections.Generic.HashSet[int]]::new()

    foreach ($cim in $cimProcesses) {
        $processId = [int]$cim.ProcessId
        if ($processId -eq 0) { continue }
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        $cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
        $cpuPercent = $null
        if ($PreviousCpu.ContainsKey($processId)) {
            $cpuDelta = [Math]::Max(0, $cpuMilliseconds - [double]$PreviousCpu[$processId])
            $cpuPercent = ($cpuDelta / $elapsedMilliseconds / $logicalProcessorCount) * 100
        }
        $PreviousCpu[$processId] = $cpuMilliseconds

        if ($roles.ContainsKey($processId)) {
            $readBytes = [long]$cim.ReadTransferCount
            $writeBytes = [long]$cim.WriteTransferCount
            $readRate = $null
            $writeRate = $null
            if ($PreviousReadBytes.ContainsKey($processId)) {
                $readRate = [Math]::Max(0, $readBytes - [long]$PreviousReadBytes[$processId]) * 1000 / $elapsedMilliseconds
            }
            if ($PreviousWriteBytes.ContainsKey($processId)) {
                $writeRate = [Math]::Max(0, $writeBytes - [long]$PreviousWriteBytes[$processId]) * 1000 / $elapsedMilliseconds
            }
            $PreviousReadBytes[$processId] = $readBytes
            $PreviousWriteBytes[$processId] = $writeBytes
            $processRows.Add([pscustomobject][ordered]@{
                id = $processId
                parentId = [int]$cim.ParentProcessId
                name = $process.ProcessName
                role = $roles[$processId]
                cpuPercent = $cpuPercent
                totalProcessorMilliseconds = $cpuMilliseconds
                privateBytes = [long]$process.PrivateMemorySize64
                workingSetBytes = [long]$process.WorkingSet64
                handles = [int]$process.HandleCount
                threads = [int]$process.Threads.Count
                readBytes = $readBytes
                writeBytes = $writeBytes
                readBytesPerSecond = $readRate
                writeBytesPerSecond = $writeRate
                startTime = try { $process.StartTime.ToUniversalTime().ToString("O") } catch { $null }
            })
        } elseif ($processId -ne $collectorProcessId -and $null -ne $cpuPercent) {
            $name = Get-NormalizedProcessName $process.ProcessName
            $externalRows.Add([pscustomobject][ordered]@{
                id = $processId
                name = $process.ProcessName
                cpuPercent = [double]$cpuPercent
                ignoredForContamination = $contaminationExclusions -contains $name
            })
            if ($contaminationExclusions -notcontains $name -and $cpuPercent -ge $ExternalCpuThresholdPercent) {
                [void]$activeExternalIds.Add($processId)
                $ExternalStreaks[$processId] = if ($ExternalStreaks.ContainsKey($processId)) { [int]$ExternalStreaks[$processId] + 1 } else { 1 }
            }
        }
    }

    foreach ($trackedId in @($ExternalStreaks.Keys)) {
        if (-not $activeExternalIds.Contains([int]$trackedId)) { $ExternalStreaks.Remove($trackedId) }
    }
    $contaminating = @($externalRows | Where-Object {
        -not $_.ignoredForContamination -and
        $ExternalStreaks.ContainsKey([int]$_.id) -and
        [int]$ExternalStreaks[[int]$_.id] -ge $consecutiveSamplesRequired
    })
    $shellRows = @($processRows | Where-Object role -eq "Shell")
    $toolRows = @($processRows | Where-Object role -eq "Tool")
    return [ordered]@{
        capturedAt = $timestamp.ToUniversalTime().ToString("O")
        processes = @($processRows)
        totalsByRole = [ordered]@{
            Shell = Get-Totals $shellRows
            Tool = Get-Totals $toolRows
            Total = Get-Totals $processRows
        }
        shellProcessCount = $shellRoots.Count
        topExternalProcesses = @($externalRows | Sort-Object cpuPercent -Descending | Select-Object -First 5)
        contaminationDetected = $contaminating.Count -gt 0
        contaminatingProcesses = @($contaminating)
    }
}

function Wait-Until([Diagnostics.Stopwatch]$Stopwatch, [double]$TargetMilliseconds) {
    while ($Stopwatch.Elapsed.TotalMilliseconds -lt $TargetMilliseconds) {
        $remaining = $TargetMilliseconds - $Stopwatch.Elapsed.TotalMilliseconds
        Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(100, [int]$remaining)))
    }
}

function Add-Reason([Collections.Generic.List[string]]$Reasons, [string]$Reason) {
    if (-not $Reasons.Contains($Reason)) { $Reasons.Add($Reason) }
}

$captureOutputDirectory = if ([string]::IsNullOrWhiteSpace($BatchId)) { $OutputDirectory } else { Join-Path $OutputDirectory $BatchId }
if (-not (Test-Path -LiteralPath $captureOutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $captureOutputDirectory -Force | Out-Null
}

$preflightCim = @(Get-CimInstance Win32_Process | Where-Object { [int]$_.SessionId -eq $collectorSessionId })
$preflightShellCount = @($preflightCim | Where-Object { $scenarioNames -contains (Get-NormalizedProcessName $_.Name) }).Count
if ($preflightShellCount -ne 1) {
    throw "Se requiere exactamente una shell de $Scenario; se encontraron $preflightShellCount."
}
$preflightTools = @(Get-ToolRootProcesses)
if ($Phase -eq "Idle") {
    $hasWezTerm = @($preflightTools | Where-Object { $wezTermNames -contains (Get-NormalizedProcessName $_.ProcessName) }).Count -gt 0
    $hasYazi = @($preflightTools | Where-Object { (Get-NormalizedProcessName $_.ProcessName) -eq "yazi" }).Count -gt 0
    if (-not $hasWezTerm -or -not $hasYazi) {
        throw "Idle requiere una instancia de WezTerm con Yazi abierta antes de medir."
    }
} elseif ($preflightTools.Count -gt 0 -and -not $AllowExistingToolsForSmoke) {
    throw "$Phase requiere comenzar sin procesos WezTerm/Yazi; se encontraron $($preflightTools.Count)."
}

$environmentMetadata = Get-EnvironmentMetadata
$runs = [Collections.Generic.List[object]]::new()
$captureReasons = [Collections.Generic.List[string]]::new()
if ($WarmupSeconds -gt 0) {
    Write-Host "Calentamiento de $WarmupSeconds segundos. No interactues con el equipo."
    Start-Sleep -Seconds $WarmupSeconds
}

for ($repetition = 1; $repetition -le $Repetitions; $repetition++) {
    Write-Host "Repeticion $repetition/$Repetitions ($Scenario, $Phase)."
    $previousCpu = @{}
    $previousReadBytes = @{}
    $previousWriteBytes = @{}
    $externalStreaks = @{}
    $previousTimestamp = Get-Date
    $samples = [Collections.Generic.List[object]]::new()
    $events = [Collections.Generic.List[object]]::new()
    $runReasons = [Collections.Generic.List[string]]::new()
    $workflowLaunched = $false
    $workflowCloseRequested = $false
    $workflowVerified = $false
    $workflowClass = "TenchyShellBenchmark-$collectorProcessId-$repetition"
    $stressLogPath = Join-Path $env:LOCALAPPDATA "TenchyShell\logs\tenchyshell.log"
    $stressLogOffset = if (Test-Path -LiteralPath $stressLogPath -PathType Leaf) {
        (Get-Item -LiteralPath $stressLogPath).Length
    } else { 0L }
    $initialToolIds = [Collections.Generic.HashSet[int]]::new()
    foreach ($tool in @(Get-ToolRootProcesses)) { [void]$initialToolIds.Add([int]$tool.Id) }
    if ($Phase -ne "Idle" -and $initialToolIds.Count -gt 0 -and -not $AllowExistingToolsForSmoke) {
        Add-Reason $runReasons "La repeticion comenzo con procesos WezTerm/Yazi ajenos al workflow."
    }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()

    for ($sampleIndex = 1; $sampleIndex -le $SamplesPerRepetition; $sampleIndex++) {
        Wait-Until $stopwatch ($sampleIndex * $IntervalMilliseconds)
        $elapsedSeconds = $stopwatch.Elapsed.TotalSeconds

        if ($Phase -eq "CommonWorkflow" -and -not $workflowLaunched -and $elapsedSeconds -ge $WorkflowLaunchSecond) {
            try {
                $separatorIndex = [Array]::IndexOf([string[]]$ToolArguments, "--")
                if ($separatorIndex -lt 1) { throw "ToolArguments debe contener '--' despues de las opciones de 'start'." }
                $launchArguments = @($ToolArguments[0..($separatorIndex - 1)]) + @("--class", $workflowClass) + @($ToolArguments[$separatorIndex..($ToolArguments.Count - 1)])
                Start-Process -FilePath $ToolCommand -ArgumentList $launchArguments | Out-Null
                $events.Add([ordered]@{ action = "LaunchWezTermYazi"; scheduledSecond = $WorkflowLaunchSecond; emittedAtSecond = $elapsedSeconds; wezTermClass = $workflowClass })
                Write-Host "[$WorkflowLaunchSecond s] WezTerm/Yazi iniciado por el workflow."
            } catch {
                Add-Reason $runReasons "No se pudo iniciar WezTerm/Yazi: $($_.Exception.Message)"
            }
            $workflowLaunched = $true
        }

        if ($Phase -eq "CommonWorkflow" -and -not $workflowCloseRequested -and $elapsedSeconds -ge $WorkflowCloseSecond) {
            $targetProcessId = $null
            try {
                $windows = @(Get-ToolRootProcesses | Where-Object {
                    -not $initialToolIds.Contains([int]$_.Id) -and
                    $wezTermNames -contains (Get-NormalizedProcessName $_.ProcessName) -and
                    $_.MainWindowHandle -ne [IntPtr]::Zero
                })
                if ($windows.Count -ne 1) { throw "Se esperaba una ventana nueva de WezTerm y se encontraron $($windows.Count)." }
                $targetProcessId = [int]$windows[0].Id
                if (-not [TenchyShellBenchmarkInput]::TrySendQuit($windows[0].MainWindowHandle)) {
                    throw "No se pudo confirmar el foco exclusivo de la ventana de prueba."
                }
            } catch {
                Add-Reason $runReasons "No se pudo solicitar a Yazi un cierre dirigido: $($_.Exception.Message)"
            }
            $events.Add([ordered]@{ action = "RequestYaziQuit"; scheduledSecond = $WorkflowCloseSecond; emittedAtSecond = $elapsedSeconds; wezTermClass = $workflowClass; targetProcessId = $targetProcessId })
            Write-Host "[$WorkflowCloseSecond s] Salida normal de Yazi solicitada en la ventana dirigida."
            $workflowCloseRequested = $true
        }

        if ($Phase -eq "CommonWorkflow" -and -not $workflowVerified -and $elapsedSeconds -ge $WorkflowVerifyClosedSecond) {
            $remainingTools = @(Get-ToolRootProcesses | Where-Object { -not $initialToolIds.Contains([int]$_.Id) })
            if ($remainingTools.Count -gt 0) {
                Add-Reason $runReasons "WezTerm/Yazi no termino dentro del plazo; no se forzo su cierre."
            }
            $events.Add([ordered]@{ action = "VerifyToolsClosed"; scheduledSecond = $WorkflowVerifyClosedSecond; emittedAtSecond = $elapsedSeconds; remainingProcessCount = $remainingTools.Count })
            $workflowVerified = $true
        }

        if ($Phase -eq "TenchyShellStress") {
            $actions = @(
                "Abrir dock: Ctrl+Alt+T",
                "Cerrar dock: Escape",
                "Cambiar al workspace 2: Ctrl+Alt+2",
                "Volver al workspace 1: Ctrl+Alt+1",
                "Abrir y cerrar el dock: Ctrl+Alt+T y Escape"
            )
            for ($actionIndex = 0; $actionIndex -lt $StressActionSeconds.Count; $actionIndex++) {
                $scheduled = $StressActionSeconds[$actionIndex]
                $alreadyEmitted = @($events | Where-Object { $_.actionIndex -eq $actionIndex }).Count -gt 0
                if (-not $alreadyEmitted -and $elapsedSeconds -ge $scheduled) {
                    $succeeded = $null
                    $actionError = $null
                    if ($ManualStressActions) {
                        Write-Host "[$scheduled s] $($actions[$actionIndex])"
                        try { [Console]::Beep(900 + ($actionIndex * 100), 120) } catch { }
                    } else {
                        try {
                            switch ($actionIndex) {
                                0 {
                                    [void][TenchyShellBenchmarkInput]::TrySendCtrlAlt(0x54)
                                    $succeeded = [TenchyShellBenchmarkInput]::WaitForTrayVisibility($true, 1000)
                                    if (-not $succeeded) { $actionError = "El dock no quedó visible tras Ctrl+Alt+T." }
                                }
                                1 {
                                    [void][TenchyShellBenchmarkInput]::TrySendEscape()
                                    $succeeded = [TenchyShellBenchmarkInput]::WaitForTrayVisibility($false, 1000)
                                    if (-not $succeeded) { $actionError = "El dock no se ocultó tras Escape." }
                                }
                                2 {
                                    [void][TenchyShellBenchmarkInput]::TrySendCtrlAlt(0x32)
                                    $succeeded = Wait-ForLogMessage $stressLogPath $stressLogOffset "Workspace activo: 2."
                                    if (-not $succeeded) { $actionError = "No se confirmó el cambio automatizado al workspace 2." }
                                }
                                3 {
                                    [void][TenchyShellBenchmarkInput]::TrySendCtrlAlt(0x31)
                                    $succeeded = Wait-ForLogMessage $stressLogPath $stressLogOffset "Workspace activo: 1."
                                    if (-not $succeeded) { $actionError = "No se confirmó el retorno automatizado al workspace 1." }
                                }
                                4 {
                                    [void][TenchyShellBenchmarkInput]::TrySendCtrlAlt(0x54)
                                    $opened = [TenchyShellBenchmarkInput]::WaitForTrayVisibility($true, 1000)
                                    if ($opened) {
                                        [void][TenchyShellBenchmarkInput]::TrySendEscape()
                                    }
                                    $closed = $opened -and [TenchyShellBenchmarkInput]::WaitForTrayVisibility($false, 1000)
                                    $succeeded = $closed
                                    if (-not $succeeded) { $actionError = "No se confirmó el segundo ciclo automatizado de apertura y cierre del dock." }
                                }
                            }
                        } catch {
                            $succeeded = $false
                            $actionError = $_.Exception.Message
                        }

                        Write-Host "[$scheduled s] Automatizado: $($actions[$actionIndex]) — $(if ($succeeded) { 'OK' } else { 'ERROR' })"
                        if (-not $succeeded) {
                            Add-Reason $runReasons "Falló la acción automatizada '$($actions[$actionIndex])': $actionError"
                        }
                    }
                    $events.Add([ordered]@{
                        action = $actions[$actionIndex]
                        actionIndex = $actionIndex
                        scheduledSecond = $scheduled
                        emittedAtSecond = $elapsedSeconds
                        automated = -not [bool]$ManualStressActions
                        succeeded = $succeeded
                        error = $actionError
                    })
                }
            }
        }

        $sample = Get-Sample $previousCpu $previousReadBytes $previousWriteBytes $externalStreaks $previousTimestamp
        if ([int]$sample.shellProcessCount -ne 1) {
            Add-Reason $runReasons "La shell dejo de ser unica durante la captura (conteo: $($sample.shellProcessCount))."
        }
        if ($sample.contaminationDetected) {
            $names = @($sample.contaminatingProcesses | ForEach-Object { "$($_.name)[$($_.id)]" }) -join ", "
            Add-Reason $runReasons "Carga externa superior a $ExternalCpuThresholdPercent % durante $ExternalCpuConsecutiveSeconds s: $names."
        }
        $previousTimestamp = [datetime]$sample.capturedAt
        $samples.Add($sample)
    }
    $stopwatch.Stop()
    foreach ($reason in $runReasons) { Add-Reason $captureReasons "Repeticion ${repetition}: $reason" }
    $runs.Add([ordered]@{
        repetition = $repetition
        valid = $runReasons.Count -eq 0
        invalidReasons = @($runReasons)
        events = @($events)
        samples = @($samples)
    })

    if ($repetition -lt $Repetitions -and $InterRepetitionSeconds -gt 0) {
        Write-Host "Reposo entre repeticiones: $InterRepetitionSeconds segundos."
        Start-Sleep -Seconds $InterRepetitionSeconds
    }
}

$result = [ordered]@{
    schemaVersion = $schemaVersion
    batchId = $BatchId
    official = -not [bool]$SmokeTest
    valid = $captureReasons.Count -eq 0
    invalidReasons = @($captureReasons)
    scenario = $Scenario
    phase = $Phase
    capturedAt = (Get-Date).ToUniversalTime().ToString("O")
    settings = [ordered]@{
        repetitions = $Repetitions
        samplesPerRepetition = $SamplesPerRepetition
        intervalMilliseconds = $IntervalMilliseconds
        warmupSeconds = $WarmupSeconds
        interRepetitionSeconds = $InterRepetitionSeconds
        workflowLaunchSecond = $WorkflowLaunchSecond
        workflowCloseSecond = $WorkflowCloseSecond
        workflowVerifyClosedSecond = $WorkflowVerifyClosedSecond
        stressActionSeconds = @($StressActionSeconds)
        stressActionsAutomated = -not [bool]$ManualStressActions
        externalCpuThresholdPercent = $ExternalCpuThresholdPercent
        externalCpuConsecutiveSeconds = $ExternalCpuConsecutiveSeconds
    }
    environment = $environmentMetadata
    processPolicy = [ordered]@{
        shellNames = $scenarioNames
        toolNames = $toolNames
        toolCommand = $ToolCommand
        toolArguments = @($ToolArguments)
        browserNames = $browserNames
        contaminationExclusions = $contaminationExclusions
        allowedExistingToolsForSmoke = [bool]$AllowExistingToolsForSmoke
        sessionId = $collectorSessionId
    }
    runs = @($runs)
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$suffix = if ($SmokeTest) { "-smoke" } else { "" }
$outputPath = Join-Path $captureOutputDirectory "$($Scenario.ToLowerInvariant())-$($Phase.ToLowerInvariant())-$timestamp$suffix.json"
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputPath -Encoding utf8
Write-Host "Muestras guardadas en: $outputPath"
if (-not $result.valid) {
    throw "La captura se guardo como invalida: $(@($captureReasons) -join ' | ')"
}
