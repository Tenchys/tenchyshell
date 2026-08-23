[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceDirectory,

    [string]$ArchivePath,

    [string]$ExpectedSha256,

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

if ([string]::IsNullOrWhiteSpace($SourceDirectory) -eq [string]::IsNullOrWhiteSpace($ArchivePath)) {
    throw "Indica exactamente uno de -SourceDirectory o -ArchivePath."
}

try {
    if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
        if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
            throw "No se encontró el ZIP del release: '$ArchivePath'."
        }
        if ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "-ExpectedSha256 debe contener el SHA-256 de 64 caracteres publicado junto al release."
        }

        $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "El checksum del ZIP no coincide; la instalación fue cancelada."
        }

        $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("TenchyShell.Install." + [Guid]::NewGuid().ToString("N"))
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $temporaryDirectory
        $SourceDirectory = $temporaryDirectory
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
