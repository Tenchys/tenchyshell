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

Write-Host "Publicación lista en: $output"
Write-Host "Comprobación: dotnet $output\TenchyShell.dll --check $output\TenchyShell.example.toml"
