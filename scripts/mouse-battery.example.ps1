$battery = Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1

if ($null -eq $battery) {
    [Console]::Out.WriteLine('{"text":"N/D","tooltip":"No se encontró una batería WMI; reemplaza este script por el de tu mouse","state":"unknown","action":"open"}')
    exit 0
}

$percent = [int]$battery.EstimatedChargeRemaining
$state = if ($percent -le 20) { 'warning' } else { 'ok' }
$result = [ordered]@{
    text = "$percent%"
    tooltip = "Batería: $percent%"
    state = $state
    action = 'open'
}

$result | ConvertTo-Json -Compress
