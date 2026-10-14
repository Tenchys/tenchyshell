# Protocolo de rendimiento externo — TenchyShell MVP 0.8.7 (esquema 3)

## Propósito y alcance

Este protocolo crea una línea base reproducible de `TenchyShell.exe` en el
escenario `Idle`, con WezTerm/Yazi como workload diario. No intenta demostrar
superioridad global con una sola máquina. `CommonWorkflow` y
`TenchyShellStress` permanecen disponibles para diagnósticos directos, pero ya
no forman parte del orquestador automático.

Los datos crudos contienen usuario, máquina, procesos y hardware. Se guardan
fuera del repositorio en:

```text
%LOCALAPPDATA%\TenchyShell\benchmarks\v3\<batch-id>\
```

Las capturas v2 se conservan como evidencia histórica, pero son inválidas para
la línea base 0.8.7. El resumidor puede abrirlas de forma explícita con
`-SchemaVersion 2`; nunca mezcla lotes v2 y v3. Los smoke tests tampoco se
pueden mezclar con un informe oficial.

## Entorno controlado

Usar el mismo usuario secundario, equipo, alimentación de CA, plan de energía,
monitores, resolución, DPI y configuración en todas las capturas. No modificar
Winlogon ni detener Explorer en la sesión principal.

Antes de comenzar:

1. Trabajar desde un commit limpio y publicar una única compilación Release.
2. Ejecutar `dotnet test TenchyShell.slnx`, comprobar la publicación con
   `--check`, ejecutar las pruebas sintéticas y superar el smoke test de
   `TenchyShell / Idle` antes de capturar el Día 0.
3. Elegir un identificador de captura, por ejemplo `20260906-idle-a`.
4. Cerrar navegador, sincronizadores y aplicaciones voluntarias; mantener
   activos los mecanismos de seguridad.
5. Cerrar sesión y volver a entrar antes de cada captura. Preparar el escenario
   ejecutando el orquestador; este espera cinco minutos antes de iniciar el
   recolector.
6. Ejecutar el recolector desde una consola diferente de WezTerm. Su PID se
   excluye de todos los agregados y su coste se publica como `observer`.

El inicio de sesión permanece manual para no almacenar credenciales ni cambiar
Winlogon. Desde ese momento toda la captura es automática: preparación,
estabilización, acciones, validaciones, cierre y recuperación de Explorer.
Las consultas a Git confían temporalmente solo en la ruta exacta del checkout;
no requieren modificar `safe.directory` en la configuración global del usuario.

El JSON registra commit, estado del árbol, Windows/build, CPU, RAM, plan de
energía, batería/CA, usuario y geometría/DPI efectivo de los monitores. El
resumidor rechaza entornos, ajustes, commits y lotes diferentes.

## Procesos, roles y métricas

El orquestador entrega un PID principal exacto: el proceso que acaba de iniciar
para TenchyShell o el propietario Win32 de `Shell_TrayWnd` para Explorer. El
recolector rechaza ese PID si está ausente, duplicado, finalizado, reutilizado,
pertenece a otra sesión o su nombre no corresponde al escenario. Una segunda
`TenchyShell.exe` nunca se confunde con el núcleo.

Cada proceso incluido tiene un rol explícito y cada muestra publica estos
agregados:

- `Core`: exclusivamente el PID principal.
- `Auxiliary`: TUI, bridge, terminal de la TUI y demás descendientes iniciados
  por el núcleo TenchyShell.
- `ShellSession`: suma de `Core` y `Auxiliary`.
- `Tool`: WezTerm/Yazi del workload y sus descendientes.
- `Total`: suma de `ShellSession` y `Tool`.

El PID PowerShell/CIM del recolector no puede aparecer en ninguno de esos
agregados. Cada muestra registra aparte su CPU, memoria privada, working set y
duración de muestreo; el informe contiene una sección `observer` que permite
evaluar su perturbación.

El hito 0.8.7 mantiene PowerShell/CIM. Un recolector Win32 solo se evaluará si
varios lotes comparables muestran que el observador ocupa una fracción material
del intervalo o altera repetidamente las métricas/P95; la mera existencia de
carga observadora no justifica añadir esa complejidad.

El recolector limita shells, herramientas y procesos externos al `SessionId`
interactivo donde se ejecuta. Una sesión de otro usuario nunca se cierra ni se
atribuye a la captura; aun así, para reducir carga global se debe cerrar la
sesión principal antes de entrar al usuario secundario.

No se atribuyen a Explorer hosts genéricos de Windows ni descendientes
arbitrarios; se conservan únicamente como contexto externo.
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

Los valores oficiales de `Idle` son cinco repeticiones, treinta muestras de un
segundo, diez segundos de calentamiento y quince segundos de reposo entre
repeticiones.

### Idle

El orquestador exige comenzar sin WezTerm/Yazi, abre una instancia identificada
y la mantiene sin interacción durante toda la captura:

```powershell
wezterm-gui.exe start --always-new-process -- yazi.exe
```

El recolector rechaza `Idle` si no encuentra ambos procesos. Al terminar, el
orquestador enfoca exclusivamente su ventana, solicita `q` a Yazi y comprueba
el cierre normal; nunca termina procesos ajenos ni fuerza el cierre de la
herramienta. La captura se conserva antes de la limpieza. Si WezTerm/Yazi no
terminan cooperativamente, el JSON queda como diagnóstico no oficial con
`cleanupCompleted=false`, se detallan los PIDs restantes y el operador debe
cerrar manualmente el workload antes de repetir una captura oficial.

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

Primero, en una sesión del usuario secundario, ejecutar el smoke test de Idle:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -SmokeTest
```

No se pulsa ninguna tecla ni se prepara manualmente WezTerm/Yazi durante el
smoke test. Si las muestras se guardan pero la limpieza falla, se conserva la
captura como diagnóstico no oficial y se detiene la ejecución; no se fuerza
ningún proceso ni se restaura Explorer automáticamente.

Para la sesión oficial, cerrar y volver a iniciar sesión antes de cada captura.
Ejecutar siempre el mismo comando; el orquestador inspecciona el lote y elige
la siguiente combinación en el orden equilibrado definido por el protocolo:

```powershell
.\scripts\invoke-performance-benchmark.ps1 -BatchId "20260820-release-a"
```

El benchmark diagnóstico ejecuta únicamente TenchyShell/Idle. Cada ejecución
usa una carpeta propia y no restaura Explorer. El análisis externo conserva las
muestras de procesos y produce deltas y posibles sospechosos por PID.

El modo interno `--automated-benchmark` solo es válido junto con
`--without-explorer` y `--exit-after-seconds`; TenchyShell conserva el
`LiveBenchmarkRecorder` durante la captura. El orquestador crea una carpeta
exclusiva por ejecución, copia el log persistente, captura stdout/stderr y
filtra la telemetría por `sessionId`. No restaura Explorer: si TenchyShell no
termina, registra el timeout y deja la recuperación posterior al procedimiento
manual de la VM o usuario secundario.
TenchyShell identifica `Shell_TrayWnd`, comprueba su PID propietario y solicita la misma salida cooperativa que ofrece la
barra de tareas. No termina Explorer a la fuerza: exige que la superficie de
shell y todos los procesos `explorer.exe` permanezcan ausentes durante dos
segundos. Un proceso residual no permite iniciar la sesión; la captura se
cancela si Windows vuelve a crear `Shell_TrayWnd`, si el HWND no coincide o si el mensaje deja de ser
compatible con una versión futura. Este
mensaje de la shell no es un contrato público de Win32, por lo que dicha
validación y la recuperación posterior son requisitos del instrumental. El log
debe confirmar que se liberaron hotkeys y recursos.

Si aparece `TenchyShell no alcanzó el estado estable sin Explorer`, no se debe
repetir la captura con una publicación antigua: actualizar el repositorio,
publicar de nuevo desde el commit limpio y ejecutar el smoke test. El mecanismo
anterior de cierre forzado provocaba que Winlogon relanzara Explorer y quedó
retirado del protocolo.

El informe también puede regenerarse explícitamente:

```powershell
$benchmarkBatch = "20260820-release-a"
$batchPath = Join-Path $env:LOCALAPPDATA "TenchyShell\benchmarks\v3\$benchmarkBatch"
.\scripts\summarize-performance.ps1 `
  -InputPath $batchPath `
  -OutputPath (Join-Path $batchPath "summary.md")
```

El resumidor v3 histórico continúa exigiendo las cinco combinaciones; las
capturas diagnósticas nuevas se revisan mediante `run-status.json` y
`resource-analysis.json` en su carpeta `runId` y no se mezclan con esa línea
base comparativa.

## Pruebas del instrumental

Las pruebas sintéticas validan cuartiles, deltas, denominador cero, matriz
completa, separación v2/v3, PID Core ausente/duplicado, clasificación de TUI,
bridge, terminal y workload, sumas exactas, exclusión del observador, captura
incompleta, menos de cinco repeticiones y entornos mezclados:

```powershell
.\scripts\test-performance.ps1
```

`-SmokeTest` en el orquestador ejecuta automáticamente una repetición corta de
Idle. Para diagnósticos de bajo nivel, el recolector conserva sus parámetros
individuales y permite ejecutar directamente las fases adicionales:

```powershell
# Diagnóstico directo; el flujo normal usa invoke-performance-benchmark.ps1.
$mainPid = (Get-Process TenchyShell | Sort-Object StartTime | Select-Object -First 1).Id
.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase Idle -MainProcessId $mainPid -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0

# Requiere comenzar sin WezTerm/Yazi.
.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase CommonWorkflow -MainProcessId $mainPid -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0 `
  -WorkflowLaunchSecond 1 -WorkflowCloseSecond 3 -WorkflowVerifyClosedSecond 5

.\scripts\measure-performance.ps1 -SmokeTest -Repetitions 1 `
  -Scenario TenchyShell -Phase TenchyShellStress -MainProcessId $mainPid -WarmupSeconds 0 `
  -SamplesPerRepetition 7 -InterRepetitionSeconds 0 `
  -StressActionSeconds 1,2,3,4,5
```

El primer ejemplo directo de `Idle` requiere preparar WezTerm/Yazi y existe
solo para investigar el recolector. No forma parte del procedimiento oficial.

## Criterio de aceptación

Esta primera comparativa establece una línea base, no un presupuesto ni la
obligación de superar a Explorer en todas las métricas. Se acepta cuando:

- las cinco repeticiones de Idle superan todas las validaciones automáticas;
- ningún agregado contiene el observador ni la telemetría interna, y el informe
  muestra `Core`, `ShellSession` y la sección separada del observador;
- no se requirió ninguna interacción durante cada captura;
- no hay bloqueos ni regresiones funcionales;
- el log confirma cierre limpio;
- cualquier crecimiento repetido de memoria, handles o hilos queda explicado
  o registrado como seguimiento;
- las limitaciones y todos los resultados válidos permanecen visibles.

Una optimización posterior se acepta únicamente si mejora repetidamente su
métrica objetivo, no empeora materialmente la cola P95 y conserva compilación,
pruebas, recuperación y comportamiento observable.
