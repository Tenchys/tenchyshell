[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\TenchyShell.App\TenchyShell.App.csproj"
$output = Join-Path $repoRoot "publish\TenchyShell\$Configuration\win-x64"

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $output

Copy-Item (Join-Path $repoRoot "config\TenchyShell.example.toml") (Join-Path $output "TenchyShell.example.toml") -Force
Copy-Item (Join-Path $repoRoot "config\TenchyShell.without-explorer.example.toml") (Join-Path $output "TenchyShell.without-explorer.example.toml") -Force

$gitCommit = $null
$gitDirty = $null
try {
    $gitCommit = (& git -C $repoRoot rev-parse HEAD 2>$null)
    $gitDirty = [bool](& git -C $repoRoot status --porcelain 2>$null)
} catch {
    # Una publicación fuera del checkout sigue siendo válida para uso normal,
    # pero el orquestador oficial rechazará un manifiesto sin commit.
}
[ordered]@{
    schemaVersion = 1
    product = "TenchyShell"
    configuration = $Configuration
    runtime = "win-x64"
    publishedAt = (Get-Date).ToUniversalTime().ToString("O")
    gitCommit = $gitCommit
    gitDirty = $gitDirty
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "benchmark-release.json") -Encoding utf8

Write-Host "Publicación lista en: $output"
Write-Host "Comprobación: $output\TenchyShell.exe --check $output\TenchyShell.example.toml"
