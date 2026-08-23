[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Install,
    [switch]$TrustForAllUsers
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$bridgeProject = Join-Path $repoRoot "src\TenchyShell.NotificationBridge\TenchyShell.NotificationBridge.csproj"
$manifest = Join-Path $repoRoot "packaging\TenchyShell.NotificationBridge\Package.appxmanifest"
$sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Recurse -Filter MakeAppx.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$signTool = Get-ChildItem -LiteralPath $sdkRoot -Recurse -Filter SignTool.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

if ([string]::IsNullOrWhiteSpace($makeAppx) -or [string]::IsNullOrWhiteSpace($signTool)) {
    throw "No se encontró MakeAppx.exe o SignTool.exe. Instala Windows SDK antes de empaquetar el bridge."
}

$outputRoot = Join-Path $repoRoot "artifacts\notification-bridge\$Configuration"
$publishDirectory = Join-Path $outputRoot "publish"
$packageDirectory = Join-Path $outputRoot "package"
$packagePath = Join-Path $outputRoot "TenchyShell.NotificationBridge.msix"
New-Item -ItemType Directory -Force -Path $outputRoot, $publishDirectory, $packageDirectory | Out-Null

dotnet publish $bridgeProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del bridge." }

Get-ChildItem -LiteralPath $publishDirectory -Force |
    Copy-Item -Destination $packageDirectory -Recurse -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $packageDirectory "AppxManifest.xml") -Force

$assetsDirectory = Join-Path $packageDirectory "Assets"
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null
# PNG válido: el bridge no se muestra en Inicio (AppListEntry=none), pero MSIX
# exige que las rutas de recursos declaradas existan.
Add-Type -AssemblyName System.Drawing
$placeholder = [Drawing.Bitmap]::new(1, 1)
try {
    foreach ($asset in "StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png") {
        $placeholder.Save((Join-Path $assetsDirectory $asset), [Drawing.Imaging.ImageFormat]::Png)
    }
}
finally { $placeholder.Dispose() }

if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
& $makeAppx pack /o /h SHA256 /d $packageDirectory /p $packagePath
if ($LASTEXITCODE -ne 0) { throw "Falló MakeAppx al crear el MSIX." }

$subject = "CN=TenchyShell Development"
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(7) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -KeyUsage DigitalSignature `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -FriendlyName "TenchyShell Notification Bridge Development"
}

$trustedCertificate = "Cert:\CurrentUser\TrustedPeople\$($certificate.Thumbprint)"
$rootCertificate = "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
$certificatePath = Join-Path $outputRoot "TenchyShell.NotificationBridge.development.cer"
if (-not (Test-Path -LiteralPath $trustedCertificate) -or -not (Test-Path -LiteralPath $rootCertificate)) {
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $trustedCertificate)) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
}
if (-not (Test-Path -LiteralPath $rootCertificate)) {
    # Solo certificado autoemitido de desarrollo; nunca se usa para releases.
    Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
}

if ($TrustForAllUsers) {
    $machineTrustedCertificate = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    if (-not (Test-Path -LiteralPath $machineTrustedCertificate)) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
    }
}

& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "Falló la firma del MSIX." }
& $signTool verify /pa $packagePath
if ($LASTEXITCODE -ne 0) {
    Write-Warning "SignTool no valida la cadena de un certificado de desarrollo en TrustedPeople; Add-AppxPackage verificará el paquete para el usuario actual."
}

if ($Install) {
    Add-AppxPackage -Path $packagePath
    Write-Host "Bridge instalado. Inícialo una vez con --request-access para conceder el permiso."
}

Write-Host "MSIX listo en: $packagePath"
