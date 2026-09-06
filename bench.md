# Benchmark oficial v3 — TenchyShell 0.8.7

Este procedimiento es para la línea base oficial. Las capturas `-SmokeTest` son
solo de validación y no deben mezclarse con el lote oficial.

## Requisitos de la sesión

Usar un usuario secundario o una máquina virtual de Windows 11 Pro.

Antes de medir:

- El árbol Git debe estar limpio (`git status --porcelain` no debe mostrar nada).
- Debe existir una publicación Release `win-x64` correspondiente al commit actual.
- Cerrar WezTerm, Yazi, navegadores, sincronizadores, juegos y aplicaciones voluntarias.
- Mantener Explorer disponible al iniciar cada captura.
- Cerrar sesión y volver a entrar antes de cada captura.
- No cambiar Winlogon ni detener Explorer manualmente.

## PowerShell recomendado

Usar PowerShell 7 (`pwsh.exe`), preferentemente la misma versión validada durante
las pruebas del repositorio. Comprobarla con:

```powershell
$PSVersionTable.PSVersion
$PSVersionTable.PSEdition
```

La versión recomendada para este procedimiento es PowerShell 7.4 o posterior.
Windows PowerShell 5.1 puede tener compatibilidad parcial, pero no es la versión
validada para la línea base oficial; no mezclar ambas ediciones dentro del mismo
lote.

## Preparar la publicación

Abrir una consola PowerShell 7 normal, distinta de WezTerm, y situarse en el
checkout:

```powershell
Set-Location C:\Users\serod\devel\TenchyShell
git status --porcelain
dotnet test TenchyShell.slnx -c Release --no-restore
.\scripts\publish.ps1
```

Comprobar que la publicación corresponde al commit actual:

```powershell
$m = Get-Content .\publish\TenchyShell\Release\win-x64\benchmark-release.json -Raw | ConvertFrom-Json
$m.gitCommit
$m.configuration
$m.runtime
$m.gitDirty
```

Debe mostrar el commit actual, `Release`, `win-x64` y `False`.

## Smoke previo obligatorio

Después de cerrar sesión y volver a entrar, ejecutar sin preparar ventanas
manualmente:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -SmokeTest
```

Las tres fases deben terminar como válidas: `Idle`, `CommonWorkflow` y
`TenchyShellStress`. Si falla una fase, no iniciar el lote oficial; corregir el
problema, republicar y repetir el smoke.

## Captura oficial del lote

Elegir un identificador nuevo y conservarlo durante las cinco ejecuciones:

```powershell
$batch = "20260906-release-a"
```

Cerrar sesión y volver a entrar antes de cada ejecución. Ejecutar exactamente el
mismo comando:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -BatchId $batch
```

El orquestador elegirá automáticamente, una combinación por ejecución:

1. `Explorer / Idle`
2. `TenchyShell / Idle`
3. `TenchyShell / CommonWorkflow`
4. `Explorer / CommonWorkflow`
5. `TenchyShell / TenchyShellStress`

Cada ejecución realiza cinco repeticiones, estabilización, acciones automáticas,
cierre y restauración de Explorer. No pulsar teclas durante la captura.

No usar `-SmokeTest`, `-AllowExistingToolsForSmoke` ni
`-ManualStressActions` en el lote oficial.

## Resumen y aceptación

Tras la quinta ejecución, generar o regenerar el informe:

```powershell
$batchPath = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v3\$batch"
.\scripts\summarize-performance.ps1 `
  -InputPath $batchPath `
  -OutputPath (Join-Path $batchPath "summary.md")
```

El informe es válido únicamente si contiene las cinco combinaciones, cinco
repeticiones válidas por captura, el mismo commit/entorno/configuración y la
orquestación completada. Debe mostrar `Core`, `Auxiliary`, `ShellSession`,
`Tool`, `Total` y la sección `observer` separada.

El PID del recolector nunca debe aparecer en los agregados. Las capturas v2 y los
smoke tests se conservan como evidencia, pero no forman parte de la línea base
0.8.7.

## Salida

Los JSON y el informe quedan en:

```text
%LOCALAPPDATA%\TenchyShell\benchmarks\v3\<batch>\
```

