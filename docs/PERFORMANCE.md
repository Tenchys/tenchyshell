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
   ejecutando el orquestador; este espera cinco minutos antes de iniciar el
   recolector.
6. Ejecutar el recolector desde una consola diferente de WezTerm, porque esa
   consola es carga observadora y no forma parte de la medición.

El inicio de sesión permanece manual para no almacenar credenciales ni cambiar
Winlogon. Desde ese momento toda la captura es automática: preparación,
estabilización, acciones, validaciones, cierre y recuperación de Explorer.

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

El orquestador exige comenzar sin WezTerm/Yazi, abre una instancia identificada
y la mantiene sin interacción durante toda la captura:

```powershell
wezterm-gui.exe start --always-new-process -- yazi.exe
```

El recolector rechaza `Idle` si no encuentra ambos procesos. Al terminar, el
orquestador enfoca exclusivamente su ventana, solicita `q` a Yazi y comprueba
el cierre normal; nunca termina procesos ajenos ni fuerza el cierre de la
herramienta.

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

Solo es válido para TenchyShell. El recolector inyecta el guion con tiempos
fijos y valida cada resultado:

- segundo 5: `Ctrl+Alt+T`, abrir dock;
- segundo 10: `Escape`, cerrar dock;
- segundo 15: `Ctrl+Alt+2`, cambiar al workspace 2;
- segundo 20: `Ctrl+Alt+1`, volver al workspace 1;
- segundo 25: `Ctrl+Alt+T` y `Escape`, abrir y cerrar el dock.

La apertura y el cierre se comprueban mediante la ventana Win32 propia del
dock. Los cambios de workspace se confirman en el log generado después del
inicio de cada repetición. Una tecla emitida sin el efecto esperado invalida la
captura. `-ManualStressActions` queda reservado a smoke tests de diagnóstico y
no se admite en datos oficiales.

## Ejecución oficial

Publicar y comprobar una única vez:

```powershell
.\scripts\publish.ps1 -Configuration Release
& .\publish\TenchyShell\Release\win-x64\TenchyShell.exe `
  --check .\publish\TenchyShell\Release\win-x64\TenchyShell.example.toml
```

La publicación incluye `benchmark-release.json`. El orquestador rechaza un
árbol sucio, un manifiesto de otra publicación o un commit distinto.

Primero, en una sesión del usuario secundario, ejecutar los tres smoke tests:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -SmokeTest
```

No se pulsa ninguna tecla ni se prepara manualmente WezTerm/Yazi durante los
smoke tests. Si una fase falla, se detiene la suite, se conserva la captura como
inválida cuando existe y se restaura Explorer.

Para la sesión oficial, cerrar y volver a iniciar sesión antes de cada captura.
Ejecutar siempre el mismo comando; el orquestador inspecciona el lote y elige
la siguiente combinación en el orden equilibrado definido por el protocolo:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -BatchId "20260820-release-a"
```

El orden automático es Explorer/Idle, TenchyShell/Idle,
TenchyShell/CommonWorkflow, Explorer/CommonWorkflow y
TenchyShell/TenchyShellStress. Cada ejecución espera cinco minutos, realiza
cinco repeticiones, cierra solo los procesos propios y restaura Explorer. Al
final informa cuál será la captura siguiente. Tras la quinta ejecución genera
`summary.md` automáticamente.

El modo interno `--automated-benchmark` solo es válido junto con
`--without-explorer` y `--exit-after-seconds`; TenchyShell restaura Explorer en
su bloque `finally`. El orquestador también mantiene una recuperación externa
y marca la captura inválida si necesita forzar el cierre de su propio proceso.
El log debe confirmar que se liberaron hotkeys y recursos.

El informe también puede regenerarse explícitamente:

```powershell
$benchmarkBatch = "20260820-release-a"
$batchPath = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v2\$benchmarkBatch"
.\scripts\summarize-performance.ps1 `
  -InputPath $batchPath `
  -OutputPath (Join-Path $batchPath "summary.md")
```

El resumidor exige las cinco combinaciones, una captura por combinación, cinco
repeticiones válidas, árbol limpio, misma publicación/commit, entorno, ajustes,
lote y orquestación automática completada.

## Pruebas del instrumental

Las pruebas sintéticas validan cuartiles, deltas, denominador cero, matriz
completa y rechazo de esquema antiguo, shell ausente/duplicada, captura
incompleta, menos de cinco repeticiones y entornos mezclados:

```powershell
.\scripts\test-performance.ps1
```

`-SmokeTest` en el orquestador ejecuta automáticamente una repetición corta de
cada fase. El resumidor la rechaza salvo que se solicite explícitamente
`-AllowSmokeTest`. Para diagnósticos de bajo nivel, el recolector conserva sus
parámetros individuales:

```powershell
# Diagnóstico directo; el flujo normal usa invoke-performance-benchmark.ps1.
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

El primer ejemplo directo de `Idle` requiere preparar WezTerm/Yazi y existe
solo para investigar el recolector. No forma parte del procedimiento oficial.

## Criterio de aceptación

Esta primera comparativa establece una línea base, no un presupuesto ni la
obligación de superar a Explorer en todas las métricas. Se acepta cuando:

- las cinco capturas superan todas las validaciones automáticas;
- no se requirió ninguna interacción durante cada captura;
- no hay bloqueos ni regresiones funcionales;
- el log confirma cierre limpio;
- cualquier crecimiento repetido de memoria, handles o hilos queda explicado
  o registrado como seguimiento;
- las limitaciones y todos los resultados válidos permanecen visibles.

Una optimización posterior se acepta únicamente si mejora repetidamente su
métrica objetivo, no empeora materialmente la cola P95 y conserva compilación,
pruebas, recuperación y comportamiento observable.
