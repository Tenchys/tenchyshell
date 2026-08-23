# Instalación

TenchyShell está dirigido a Windows 11 Pro. No modifica Winlogon ni reemplaza
el compositor DWM; `explorer.exe` se conserva como recuperación explícita.

## Requisitos

| Elemento | Cuándo es necesario | Propósito |
| --- | --- | --- |
| Windows 11 Pro | Siempre | Plataforma objetivo. |
| PowerShell | Siempre | Instalador, scripts y diagnóstico. |
| .NET SDK 10 | Solo desde código fuente | Restaurar, compilar y ejecutar proyectos. |
| WinGet y red | Solo instalador con dependencias faltantes | Instalar WezTerm y Yazi oficiales. |
| WezTerm | Recomendado | Terminal configurado por defecto. |
| Yazi | Recomendado | Gestor de archivos lanzado dentro de la terminal. |

El release `win-x64` es auto-contenido: no requiere el SDK ni runtime de .NET.
El navegador predeterminado es `msedge.exe`, pero puede cambiarse en TOML.

## Desde un release

Descarga `Install-TenchyShell.ps1` desde el GitHub Release deseado y ejecútalo
con su tag. El bootstrapper descarga los assets del mismo release, valida el
checksum y realiza la instalación:

```powershell
.\Install-TenchyShell.ps1 -ReleaseTag vX.Y.Z
```

También puedes descargar manualmente el ZIP `win-x64`, su checksum `.sha256` y
el bootstrapper. Valida e instala desde la carpeta de descarga:

```powershell
$hash = Get-Content .\TenchyShell-vX.Y.Z-win-x64.zip.sha256
.\Install-TenchyShell.ps1 -ArchivePath .\TenchyShell-vX.Y.Z-win-x64.zip -ExpectedSha256 $hash
```

El instalador escribe bajo `%LOCALAPPDATA%\TenchyShell\app`, conserva la
configuración del usuario y detecta WezTerm/Yazi. Usa `-SkipDependencies` si
ya administras esas aplicaciones, o `-WhatIf` para inspeccionar sin cambios.
Repite el comando con un tag nuevo para actualizar el binario sin sobrescribir
el TOML ni scripts personalizados del usuario.

Verifica la instalación sin iniciar el shell:

```powershell
& "$env:LOCALAPPDATA\TenchyShell\app\TenchyShell.exe" --check
```

## Desde el código fuente

```powershell
git clone https://github.com/Tenchys/tenchyshell.git
Set-Location tenchyshell
dotnet restore TenchyShell.slnx
dotnet build TenchyShell.slnx -c Release
.\scripts\publish.ps1 -Configuration Release
```

La publicación queda en `publish\TenchyShell\Release\win-x64\`. Ejecútala
con un TOML explícito:

```powershell
.\publish\TenchyShell\Release\win-x64\TenchyShell.exe .\config\TenchyShell.example.toml
```

## Actualización y desinstalación

Para actualizar, instala el release nuevo con el mismo bootstrapper: no borra
la configuración, estado ni logs existentes. Cierra TenchyShell normalmente
antes de cambiar de versión.

Para una desinstalación manual, elimina solamente
`%LOCALAPPDATA%\TenchyShell\app`. Conserva `config`, `logs`, `state` y
`benchmarks` hasta decidir si también deseas retirar tus datos. El bridge de
notificaciones es un MSIX aparte; consulta [Notificaciones](NOTIFICATIONS.md)
para quitarlo con `Remove-AppxPackage`.
