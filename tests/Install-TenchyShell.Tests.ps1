Describe "Install-TenchyShell.ps1" {
    BeforeEach {
        $installer = Join-Path $PSScriptRoot "..\\scripts\\Install-TenchyShell.ps1"
        $release = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $scripts = Join-Path $release "scripts"
        New-Item -ItemType Directory -Path $scripts -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $release "TenchyShell.exe") | Out-Null
        Set-Content -LiteralPath (Join-Path $release "TenchyShell.example.toml") -Value "[applications]`nbrowser = 'msedge.exe'" -Encoding utf8
        Set-Content -LiteralPath (Join-Path $scripts "mouse-battery.example.ps1") -Value "Write-Output 'example'" -Encoding utf8
        $installDirectory = Join-Path $TestDrive "installed"
        $userConfig = Join-Path $TestDrive ".config\\tenchyshell\\config.toml"
    }

    It "requires exactly one source input" {
        { & $installer -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*exactamente uno*"

        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        { & $installer -SourceDirectory $release -ArchivePath $archive -ExpectedSha256 $hash -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*exactamente uno*"

        { & $installer -SourceDirectory $release -ReleaseTag "v0.7.12" -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*exactamente uno*"
    }

    It "rejects a missing archive, an invalid checksum, and a mismatched checksum" {
        { & $installer -ArchivePath (Join-Path $TestDrive "missing.zip") -ExpectedSha256 ("0" * 64) -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*No se encontró el ZIP*"

        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        { & $installer -ArchivePath $archive -ExpectedSha256 "invalid" -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*SHA-256*"
        { & $installer -ArchivePath $archive -ExpectedSha256 ("0" * 64) -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*checksum del ZIP no coincide*"
    }

    It "rejects a release without the required executable or configuration template" {
        Remove-Item -LiteralPath (Join-Path $release "TenchyShell.exe")
        { & $installer -SourceDirectory $release -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*No se encontró TenchyShell.exe*"

        New-Item -ItemType File -Path (Join-Path $release "TenchyShell.exe") | Out-Null
        Remove-Item -LiteralPath (Join-Path $release "TenchyShell.example.toml")
        { & $installer -SourceDirectory $release -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig } |
            Should -Throw "*No se encontró TenchyShell.example.toml*"
    }

    It "does not write during WhatIf" {
        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        $temporaryDirectoriesBefore = @(Get-ChildItem -Path ([IO.Path]::GetTempPath()) -Directory -Filter "TenchyShell.Install.*" | Select-Object -ExpandProperty FullName)

        & $installer -ArchivePath $archive -ExpectedSha256 $hash -SkipDependencies -WhatIf -InstallDirectory $installDirectory -UserConfigPath $userConfig

        Test-Path -LiteralPath $installDirectory | Should -BeFalse
        Test-Path -LiteralPath $userConfig | Should -BeFalse
        $temporaryDirectoriesAfter = @(Get-ChildItem -Path ([IO.Path]::GetTempPath()) -Directory -Filter "TenchyShell.Install.*" | Select-Object -ExpandProperty FullName)
        $temporaryDirectoriesAfter | Should -Be $temporaryDirectoriesBefore
    }

    It "installs a verified archive and creates the initial configuration" {
        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

        & $installer -ArchivePath $archive -ExpectedSha256 $hash -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig

        Test-Path -LiteralPath (Join-Path $installDirectory "TenchyShell.exe") | Should -BeTrue
        Test-Path -LiteralPath $userConfig | Should -BeTrue
        Test-Path -LiteralPath (Join-Path (Split-Path -Parent $userConfig) "scripts\\mouse-battery.example.ps1") | Should -BeTrue
    }

    It "downloads, verifies and installs a release tag" {
        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

        Mock Invoke-WebRequest {
            param($Uri, $OutFile)
            if ($Uri.AbsoluteUri.EndsWith(".sha256")) {
                Set-Content -LiteralPath $OutFile -Value $hash -NoNewline
            } else {
                Copy-Item -LiteralPath $archive -Destination $OutFile
            }
        }

        & $installer -ReleaseTag "v0.7.12" -Repository "Tenchys/tenchyshell" -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig

        Test-Path -LiteralPath (Join-Path $installDirectory "TenchyShell.exe") | Should -BeTrue
    }

    It "validates a release tag in WhatIf without installing it" {
        $archive = Join-Path $release "release.zip"
        Compress-Archive -Path (Join-Path $release "*") -DestinationPath $archive
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

        Mock Invoke-WebRequest {
            param($Uri, $OutFile)
            if ($Uri.AbsoluteUri.EndsWith(".sha256")) {
                Set-Content -LiteralPath $OutFile -Value $hash -NoNewline
            } else {
                Copy-Item -LiteralPath $archive -Destination $OutFile
            }
        }

        & $installer -ReleaseTag "v0.7.12.1" -Repository "Tenchys/tenchyshell" -SkipDependencies -WhatIf -InstallDirectory $installDirectory -UserConfigPath $userConfig

        Test-Path -LiteralPath $installDirectory | Should -BeFalse
        Test-Path -LiteralPath $userConfig | Should -BeFalse
    }

    It "preserves an existing configuration and example script" {
        $userScripts = Join-Path (Split-Path -Parent $userConfig) "scripts"
        New-Item -ItemType Directory -Path $userScripts -Force | Out-Null
        Set-Content -LiteralPath $userConfig -Value "[applications]`nbrowser = 'custom.exe'" -Encoding utf8
        $existingScript = Join-Path $userScripts "mouse-battery.example.ps1"
        Set-Content -LiteralPath $existingScript -Value "custom script" -Encoding utf8

        & $installer -SourceDirectory $release -SkipDependencies -InstallDirectory $installDirectory -UserConfigPath $userConfig

        (Get-Content -LiteralPath $userConfig -Raw).TrimEnd() | Should -Be "[applications]`nbrowser = 'custom.exe'"
        (Get-Content -LiteralPath $existingScript -Raw).TrimEnd() | Should -Be "custom script"
    }
}
