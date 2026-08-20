# Protocolo de rendimiento — TenchyShell MVP 0.7.6

## Propósito y alcance

Este protocolo crea una línea base reproducible entre `explorer.exe` y
`TenchyShell.exe`. No intenta demostrar superioridad global con una sola
máquina. La carga comparable se limita a reposo y a un ciclo neutral de
WezTerm/Yazi; el dock y los workspaces se miden aparte en
`TenchyShellStress` y nunca se usan para calcular deltas contra Explorer.

Los datos crudos contienen usuario, máquina, procesos y hardware. Se guardan
fuera del repositorio en:

```text
%LOCALAPPDATA%\TenchyShell\benchmarks\v2\<batch-id>\
```

El esquema 1 y los smoke tests no se pueden mezclar con un informe oficial.

## Entorno controlado

Usar el mismo usuario secundario, equipo, alimentación de CA, plan de energía,
monitores, resolución, DPI y configuración en todas las capturas. No modificar
Winlogon ni detener Explorer en la sesión principal.

Antes de comenzar:

1. Trabajar desde un commit limpio y publicar una única compilación Release.
2. Ejecutar `dotnet test TenchyShell.slnx` y comprobar la publicación con
   `--check`.
3. Elegir un identificador de lote, por ejemplo `20260820-release-a`, y
   conservarlo para las cinco capturas.
4. Cerrar navegador, sincronizadores y aplicaciones voluntarias; mantener
   activos los mecanismos de seguridad.
5. Cerrar sesión y volver a entrar antes de cada captura. Preparar el escenario
   y dejarlo cinco minutos en reposo antes de iniciar el recolector.
6. Ejecutar el recolector desde una consola diferente de WezTerm, porque esa
   consola es carga observadora y no forma parte de la medición.

El JSON registra commit, estado del árbol, Windows/build, CPU, RAM, plan de
energía, batería/CA, usuario y geometría/DPI efectivo de los monitores. El
resumidor rechaza entornos, ajustes, commits y lotes diferentes.

## Procesos, roles y métricas

Cada proceso incluido tiene un rol explícito:

- `Shell`: exactamente un `TenchyShell.exe` —se acepta `MinimalShell.exe` solo
  como compatibilidad histórica— o exactamente un `explorer.exe`.
- `Tool`: WezTerm, Yazi y descendientes de estas herramientas.
- `Total`: suma de `Shell` y `Tool`.

No se atribuyen a Explorer sus hosts auxiliares ni descendientes arbitrarios.
Los navegadores se excluyen de los roles. Procesos externos se conservan como
contexto; una aplicación no prevista que supere el 5 % de CPU durante diez
segundos invalida la captura. DWM y los hosts conocidos de la shell de Windows
se registran, pero no contaminan por sí solos una acción esperada.

Por PID se capturan CPU acumulada y por intervalo, memoria privada, working
set, handles, hilos, I/O, inicio y relación padre-hijo. El primer valor nulo de
CPU/I/O de cada repetición se excluye, nunca se convierte en cero.
El porcentaje de CPU se normaliza por procesadores lógicos: 100 % representa
la capacidad total de la máquina, no un único núcleo.

El informe calcula primero la mediana de cada repetición y después:

- mediana de las cinco medianas, cifra comparativa principal;
- IQR de las medianas, como dispersión entre repeticiones;
- P95 de todas las muestras válidas;
- mínimo y máximo de las medianas;
- delta absoluto y porcentual, mostrando `N/D` si Explorer vale cero.

## Fases

Los valores oficiales son cinco repeticiones, treinta muestras de un segundo,
diez segundos de calentamiento y quince segundos de reposo entre repeticiones.

### Idle

Debe existir una instancia de WezTerm con Yazi antes de medir. Se mantiene
abierta y sin interacción durante toda la captura:

```powershell
wezterm-gui.exe start --always-new-process -- yazi.exe
```

El recolector rechaza `Idle` si no encuentra ambos procesos.

### CommonWorkflow

Debe comenzar sin WezTerm ni Yazi. En cada repetición el recolector:

- segundo 5: abre una instancia nueva de WezTerm con Yazi;
- segundo 20: localiza la única ventana nueva de WezTerm, confirma que obtiene
  el foco y envía la tecla `Q` con la que Yazi termina normalmente;
- segundo 25: verifica que los procesos propios terminaron.

Nunca fuerza el cierre. Si no puede identificar y enfocar una única ventana,
Yazi no termina normalmente o quedan procesos al vencer el plazo, guarda el
JSON como inválido.

### TenchyShellStress

Solo es válido para TenchyShell. El recolector muestra y marca acústicamente el
guion; el operador ejecuta:

- segundo 5: `Ctrl+Alt+T`, abrir dock;
- segundo 10: `Escape`, cerrar dock;
- segundo 15: `Ctrl+Alt+2`, cambiar al workspace 2;
- segundo 20: `Ctrl+Alt+1`, volver al workspace 1;
- segundo 25: `Ctrl+Alt+T` y `Escape`, abrir y cerrar el dock.

## Ejecución oficial

Publicar y comprobar una única vez:

```powershell
.\scripts\publish.ps1 -Configuration Release
dotnet .\publish\TenchyShell\Release\win-x64\TenchyShell.dll `
  --check .\publish\TenchyShell\Release\win-x64\TenchyShell.example.toml
```

En cada sesión TenchyShell del usuario secundario:

```powershell
dotnet .\publish\TenchyShell\Release\win-x64\TenchyShell.dll `
  --without-explorer `
  .\publish\TenchyShell\Release\win-x64\TenchyShell.without-explorer.example.toml
```

Escribir `DETENER`, confirmar que `explorer.exe` desaparece y conservar abierta
esa consola. En las sesiones Explorer, no iniciar TenchyShell.

Ejecutar exactamente en este orden, cerrando y reabriendo la sesión entre
comandos. Sustituir el identificador por el elegido para el lote:

```powershell
$benchmarkBatch = "20260820-release-a"

# 1. Explorer / Idle; preparar WezTerm+Yazi y esperar cinco minutos.
.\scripts\measure-performance.ps1 `
  -BatchId $benchmarkBatch -Scenario Explorer -Phase Idle

# 2. TenchyShell / Idle; preparar WezTerm+Yazi y esperar cinco minutos.
.\scripts\measure-performance.ps1 `
  -BatchId $benchmarkBatch -Scenario TenchyShell -Phase Idle

# 3. TenchyShell / CommonWorkflow; comenzar sin WezTerm/Yazi.
.\scripts\measure-performance.ps1 `
  -BatchId $benchmarkBatch -Scenario TenchyShell -Phase CommonWorkflow

# 4. Explorer / CommonWorkflow; comenzar sin WezTerm/Yazi.
.\scripts\measure-performance.ps1 `
  -BatchId $benchmarkBatch -Scenario Explorer -Phase CommonWorkflow

# 5. TenchyShell / TenchyShellStress; comenzar sin WezTerm/Yazi.
.\scripts\measure-performance.ps1 `
  -BatchId $benchmarkBatch -Scenario TenchyShell -Phase TenchyShellStress
```

Después de cerrar TenchyShell, revisar
`%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log` y confirmar que liberó
hotkeys y recursos. Generar el informe seleccionando solo el directorio del
lote:

```powershell
$batchPath = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v2\$benchmarkBatch"
.\scripts\summarize-performance.ps1 `
  -InputPath $batchPath `
  -OutputPath (Join-Path $batchPath "summary.md")
```

El resumidor exige las cinco combinaciones, una captura por combinación, cinco
repeticiones válidas, árbol limpio, mismo commit, entorno, ajustes y lote.

## Pruebas del instrumental

Las pruebas sintéticas validan cuartiles, deltas, denominador cero, matriz
completa y rechazo de esquema antiguo, shell ausente/duplicada, captura
incompleta, menos de cinco repeticiones y entornos mezclados:

```powershell
.\scripts\test-performance.ps1
```

`-SmokeTest` permite exactamente una repetición no oficial. El resumidor la
rechaza salvo que se solicite explícitamente `-AllowSmokeTest`. Para reducir la
duración se pueden ajustar las marcas manteniendo su orden:

En una sesión de desarrollo que ya tenga WezTerm abierto puede añadirse
`-AllowExistingToolsForSmoke`. Esta excepción nunca es válida para una captura
oficial y el cierre automatizado se limita a los procesos creados por el
workflow.

```powershell
# Requiere WezTerm/Yazi ya abiertos.
.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase Idle -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0

# Requiere comenzar sin WezTerm/Yazi.
.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase CommonWorkflow -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0 `
  -WorkflowLaunchSecond 1 -WorkflowCloseSecond 3 -WorkflowVerifyClosedSecond 5

.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase TenchyShellStress -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0 `
  -StressActionSeconds 1,2,3,4,5
```

## Criterio de aceptación

Esta primera comparativa establece una línea base, no un presupuesto ni la
obligación de superar a Explorer en todas las métricas. Se acepta cuando:

- las cinco capturas superan todas las validaciones automáticas;
- no hay bloqueos ni regresiones funcionales;
- el log confirma cierre limpio;
- cualquier crecimiento repetido de memoria, handles o hilos queda explicado
  o registrado como seguimiento;
- las limitaciones y todos los resultados válidos permanecen visibles.

Una optimización posterior se acepta únicamente si mejora repetidamente su
métrica objetivo, no empeora materialmente la cola P95 y conserva compilación,
pruebas, recuperación y comportamiento observable.
