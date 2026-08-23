[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceDirectory,

    [string]$ArchivePath,

    [string]$ExpectedSha256,

    [string]$ReleaseTag,

    [string]$Repository = "Tenchys/tenchyshell",

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "TenchyShell\\app"),

    # Permite ejecutar pruebas aisladas sin modificar el perfil del usuario.
    [string]$UserConfigPath,

    [switch]$SkipDependencies
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($UserConfigPath)) {
    $profileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $userConfig = Join-Path $profileDirectory ".config\\tenchyshell\\config.toml"
} else {
    $userConfig = $UserConfigPath
}
$temporaryDirectory = $null

if (@($SourceDirectory, $ArchivePath, $ReleaseTag | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -ne 1) {
    throw "Indica exactamente uno de -SourceDirectory, -ArchivePath o -ReleaseTag."
}

try {
    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') {
            throw "-Repository debe tener el formato propietario/repositorio."
        }
        if ($ReleaseTag -match '[\\/:*?"<>|\s]') {
            throw "-ReleaseTag contiene caracteres no válidos."
        }

        $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.Install." + [Guid]::NewGuid().ToString("N"))
        # La descarga y validación se realizan también con -WhatIf; el temporal
        # es interno, se elimina en finally y no representa una instalación.
        New-Item -ItemType Directory -Path $temporaryDirectory -Force -WhatIf:$false | Out-Null
        $assetBaseName = "TenchyShell-$ReleaseTag-win-x64"
        $releaseBaseUri = "https://github.com/$Repository/releases/download/$ReleaseTag"
        $ArchivePath = Join-Path $temporaryDirectory "$assetBaseName.zip"
        $checksumPath = "$ArchivePath.sha256"

        Write-Host "Descargando release $ReleaseTag desde GitHub..."
        Invoke-WebRequest -Uri "$releaseBaseUri/$assetBaseName.zip" -OutFile $ArchivePath
        Invoke-WebRequest -Uri "$releaseBaseUri/$assetBaseName.zip.sha256" -OutFile $checksumPath
        $ExpectedSha256 = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
        if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
            throw "No se encontró el ZIP del release: '$ArchivePath'."
        }
        if ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "-ExpectedSha256 debe contener el SHA-256 de 64 caracteres publicado junto al release."
        }

        # Get-FileHash respeta $WhatIfPreference en Windows PowerShell y no
        # devuelve un hash cuando el instalador se invoca con -WhatIf. La
        # comprobación es estrictamente de lectura, por lo que debe ejecutarse
        # antes de simular las operaciones que sí modifican el sistema.
        $previousWhatIfPreference = $WhatIfPreference
        try {
            $WhatIfPreference = $false
            $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
        }
        finally {
            $WhatIfPreference = $previousWhatIfPreference
        }
        if (-not [string]::Equals($actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "El checksum del ZIP no coincide; la instalación fue cancelada."
        }

        if ($null -eq $temporaryDirectory) {
            $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.Install." + [Guid]::NewGuid().ToString("N"))
        }
        $extractionDirectory = Join-Path $temporaryDirectory "release"
        # La extracción temporal valida el contenido del ZIP aun en modo
        # simulación; finally la elimina siempre sin dejar residuos.
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extractionDirectory -WhatIf:$false
        $SourceDirectory = $extractionDirectory
    }

    $releaseExecutable = Join-Path $SourceDirectory "TenchyShell.exe"
    $templatePath = Join-Path $SourceDirectory "TenchyShell.example.toml"
    $templateScripts = Join-Path $SourceDirectory "scripts"

    if (-not (Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) {
        throw "No se encontró TenchyShell.exe en '$SourceDirectory'."
    }
    if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
        throw "No se encontró TenchyShell.example.toml en '$SourceDirectory'."
    }

function Test-CommandAvailable([string]$CommandName) {
    return $null -ne (Get-Command $CommandName -ErrorAction SilentlyContinue)
}

function Install-WingetDependency([string]$PackageId, [string]$DisplayName, [string]$CommandName) {
    if (Test-CommandAvailable $CommandName) {
        Write-Host "$DisplayName ya está disponible ($CommandName)."
        return
    }

    if (-not (Test-CommandAvailable "winget.exe")) {
        throw "No se encontró WinGet y falta $DisplayName. Instala App Installer/WinGet o vuelve a ejecutar con -SkipDependencies tras instalarlo manualmente."
    }

    if ($PSCmdlet.ShouldProcess($DisplayName, "Instalar con winget ($PackageId)")) {
        & winget.exe install --id $PackageId --exact --source winget --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "WinGet no pudo instalar $DisplayName (código $LASTEXITCODE)."
        }
    }
}

    if (-not $SkipDependencies) {
        Install-WingetDependency "wez.wezterm" "WezTerm" "wezterm-gui.exe"
        Install-WingetDependency "sxyazi.yazi" "Yazi" "yazi.exe"
    }

    if ($PSCmdlet.ShouldProcess($InstallDirectory, "Instalar TenchyShell")) {
        New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
        Copy-Item -Path (Join-Path $SourceDirectory "*") -Destination $InstallDirectory -Recurse -Force
    }

    if (-not (Test-Path -LiteralPath $userConfig -PathType Leaf)) {
        if ($PSCmdlet.ShouldProcess($userConfig, "Crear configuración inicial")) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $userConfig) -Force | Out-Null
            Copy-Item -LiteralPath $templatePath -Destination $userConfig
        }
        Write-Host "Configuración inicial preparada en: $userConfig"
    } else {
        Write-Host "Se conserva la configuración existente: $userConfig"
    }

    # Los scripts de ejemplo se resuelven de forma relativa a config.toml.
    # Se copian únicamente si no existían para no modificar personalizaciones.
    if (Test-Path -LiteralPath $templateScripts -PathType Container) {
        $userScripts = Join-Path (Split-Path -Parent $userConfig) "scripts"
        foreach ($scriptFile in Get-ChildItem -LiteralPath $templateScripts -File) {
            $destination = Join-Path $userScripts $scriptFile.Name
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -and
                $PSCmdlet.ShouldProcess($destination, "Copiar script de ejemplo")) {
                New-Item -ItemType Directory -Path $userScripts -Force | Out-Null
                Copy-Item -LiteralPath $scriptFile.FullName -Destination $destination
            }
        }
    }

    Write-Host "TenchyShell instalado en: $InstallDirectory"
    Write-Host "Verifica dependencias con: & '$InstallDirectory\\TenchyShell.exe' --check"
}
finally {
    if ($null -ne $temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        # La extracción sirve para validar un ZIP incluso con -WhatIf; esta
        # limpieza es interna y nunca debe quedar anulada por ShouldProcess.
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -WhatIf:$false -Confirm:$false
    }
}
