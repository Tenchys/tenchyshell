[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\TenchyShell.NotificationTestSender\TenchyShell.NotificationTestSender.csproj"
$manifest = Join-Path $repoRoot "packaging\TenchyShell.NotificationTestSender\Package.appxmanifest"
$sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Recurse -Filter MakeAppx.exe | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$signTool = Get-ChildItem -LiteralPath $sdkRoot -Recurse -Filter SignTool.exe | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($makeAppx) -or [string]::IsNullOrWhiteSpace($signTool)) { throw "Falta Windows SDK (MakeAppx o SignTool)." }

$outputRoot = Join-Path $repoRoot "artifacts\notification-test-sender\$Configuration"
$publishDirectory = Join-Path $outputRoot "publish"
$packageDirectory = Join-Path $outputRoot "package"
$packagePath = Join-Path $outputRoot "TenchyShell.NotificationTestSender.msix"
New-Item -ItemType Directory -Force -Path $outputRoot, $publishDirectory, $packageDirectory | Out-Null
dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true -p:PublishSingleFile=false --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del emisor de prueba." }
Get-ChildItem -LiteralPath $publishDirectory -Force | Copy-Item -Destination $packageDirectory -Recurse -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $packageDirectory "AppxManifest.xml") -Force

$assetsDirectory = Join-Path $packageDirectory "Assets"
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null
# PNG real y visible; permite validar el logo extraído desde AppInfo.DisplayInfo.GetLogo.
Add-Type -AssemblyName System.Drawing
$preview = [Drawing.Bitmap]::new(64, 64)
$graphics = [Drawing.Graphics]::FromImage($preview)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(0, 120, 212))
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, 5)
    try { $graphics.DrawEllipse($pen, 14, 14, 36, 36) }
    finally { $pen.Dispose() }
    foreach ($asset in "StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png") {
        $preview.Save((Join-Path $assetsDirectory $asset), [Drawing.Imaging.ImageFormat]::Png)
    }
}
finally {
    $graphics.Dispose()
    $preview.Dispose()
}
if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
& $makeAppx pack /o /h SHA256 /d $packageDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) { throw "Falló MakeAppx." }

$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=TenchyShell Development" -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($null -eq $certificate) { throw "No se encontró el certificado de desarrollo TenchyShell. Ejecuta primero package-notification-bridge.ps1." }
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "Falló la firma del MSIX." }
if ($Install) { Add-AppxPackage -Path $packagePath; Write-Host "Emisor de prueba instalado." }
Write-Host "MSIX listo en: $packagePath"
