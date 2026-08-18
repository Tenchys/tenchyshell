[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\MinimalShell.App\MinimalShell.App.csproj"
$output = Join-Path $repoRoot "publish\$Configuration\win-x64"

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $output

Copy-Item (Join-Path $repoRoot "config\MinimalShell.example.toml") (Join-Path $output "MinimalShell.example.toml") -Force
Copy-Item (Join-Path $repoRoot "config\MinimalShell.without-explorer.example.toml") (Join-Path $output "MinimalShell.without-explorer.example.toml") -Force

Write-Host "Publicación lista en: $output"
Write-Host "Comprobación: dotnet $output\MinimalShell.dll --check $output\MinimalShell.example.toml"
