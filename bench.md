# Benchmark diagnóstico — TenchyShell Idle

Este procedimiento es un benchmark externo de diagnóstico. Cada ejecución es
independiente y deja la sesión sin restauración automática; las capturas no
deben ejecutarse en la sesión principal.

## Requisitos de la sesión

Usar un usuario secundario o una máquina virtual de Windows 11 Pro.

Antes de medir:

- El árbol Git debe estar limpio (`git status --porcelain` no debe mostrar nada).
- Debe existir una publicación Release `win-x64` correspondiente al commit actual.
- Cerrar WezTerm, Yazi, navegadores, sincronizadores, juegos y aplicaciones voluntarias.
- Ejecutar cada captura en una VM o usuario secundario preparado para quedar sin Explorer.
- Cerrar sesión y volver a entrar antes de cada captura.
- No cambiar Winlogon ni restaurar Explorer desde el benchmark.

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

El smoke ejecuta únicamente `TenchyShell / Idle`. Si la captura se guarda pero
WezTerm/Yazi no terminan cooperativamente, el JSON queda como diagnóstico no
oficial y el estado registra los PIDs pendientes; cerrar manualmente el árbol
antes de repetir una captura oficial.

## Captura diagnóstica

Elegir un identificador para relacionar la ejecución:

```powershell
$batch = "20260906-release-a"
```

Cerrar sesión y volver a entrar en la VM o usuario secundario. Ejecutar:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -BatchId $batch
```

El orquestador inicia TenchyShell con `--without-explorer`, verifica que la
superficie `Shell_TrayWnd` desaparece, abre WezTerm/Yazi para el workload Idle,
mide procesos hijos y conserva el log, stdout/stderr y telemetría filtrada por
`sessionId`. `resource-analysis.json` contiene deltas inicial-final y marca
crecimiento sostenido de memoria, handles o hilos. No pulsar teclas durante la
captura.

La captura se guarda antes de la limpieza. Si el foco o la salida cooperativa
de Yazi fallan, el orquestador no fuerza ningún proceso: conserva el JSON,
marca `cleanupCompleted=false` en el diagnóstico y devuelve error para impedir
que se trate como resultado oficial. Cerrar manualmente únicamente los
procesos del workload antes de repetirla.

Si TenchyShell no termina, se registra el timeout y se cierra únicamente su
proceso propio. El benchmark nunca inicia, mata ni restaura Explorer. La
recuperación posterior es manual mediante el hotkey configurado o el
procedimiento de la VM.

## Salida

Los JSON y el informe quedan en:

```text
%LOCALAPPDATA%\TenchyShell\benchmarks\diagnostic\<runId>\
```
