[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$minimumPesterVersion = [Version]"5.0.0"
$pester = Get-Module -ListAvailable Pester |
    Where-Object { $_.Version -ge $minimumPesterVersion } |
    Sort-Object Version -Descending |
    Select-Object -First 1

if ($null -eq $pester) {
    throw "Se requiere Pester $minimumPesterVersion o posterior. Instálalo con Install-Module Pester -Scope CurrentUser."
}

Import-Module $pester.Path -Force
$testPath = Join-Path $PSScriptRoot "..\\tests\\Install-TenchyShell.Tests.ps1"
$result = Invoke-Pester -Path $testPath -PassThru
if ($result.FailedCount -ne 0) {
    exit 1
}
