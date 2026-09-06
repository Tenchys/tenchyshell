# Hallazgos y optimizaciones futuras

## Propósito

Este documento conserva hipótesis y observaciones que podrían reducir el coste
de recursos de TenchyShell en hitos futuros. El objetivo es que reemplazar
Explorer tenga una ventaja material también en equipos antiguos, manteniendo
la estabilidad, la configurabilidad y la recuperación explícita mediante
`explorer.exe`.

El hito actual está dedicado a estabilidad y validación en un flujo real de
trabajo. Las entradas de este documento no autorizan cambios de comportamiento
ni se consideran optimizaciones demostradas. Cuando una propuesta se programe,
debe medirse con el protocolo de [`PERFORMANCE.md`](PERFORMANCE.md). Los cambios
aceptados y sus resultados se registran en
[`OPTIMIZATIONS.md`](OPTIMIZATIONS.md).

## Criterios comunes

Cada propuesta debe cumplir estas condiciones antes de promoverse:

- definir una hipótesis, una métrica objetivo y una carga reproducible;
- obtener una medición Release anterior y posterior en el mismo entorno;
- no empeorar materialmente el P95, la estabilidad ni la respuesta al teclado;
- mantener la sesión utilizable sin Explorer y conservar su hotkey de
  recuperación;
- preservar la configuración y la migración idempotente desde `MinimalShell`;
- evitar polling, cachés o procesos auxiliares nuevos sin una mejora medida.

Conviene informar dos ámbitos por separado:

1. **Núcleo del shell:** proceso encargado del message loop, hotkeys, ventanas,
   workspaces, foco y recuperación.
2. **Sesión TenchyShell:** núcleo más TUI, bridge de notificaciones y cualquier
   otro proceso auxiliar iniciado por la configuración evaluada.

## Observación diagnóstica inicial

Dos capturas locales Debug del 2 de septiembre de 2026 mostraron un posible
coste fijo que merece atribución, pero no constituyen una línea base oficial:

- el proceso principal acumuló aproximadamente 23,5 segundos de CPU durante
  302 segundos, equivalentes a cerca del 7,8 % de un núcleo;
- el conjunto observado rondó 270 MiB de working set: aproximadamente 157 MiB
  del proceso principal, 60 MiB de otro proceso `TenchyShell.exe` y 54 MiB del
  bridge de notificaciones;
- la captura incluyó tres procesos, se tomó desde un árbol de desarrollo y no
  identifica por sí sola qué subsistema produjo el consumo.

Estos valores sirven solamente para formular investigaciones. No deben
compararse con Explorer ni citarse como resultado de rendimiento de una
versión publicada.

## Backlog de investigaciones

### FUT-001 — Atribuir el coste base con ETW

- **Estado:** pendiente de medición.
- **Observación:** el porcentaje total de CPU puede parecer pequeño en un
  procesador moderno aunque represente trabajo apreciable de un solo núcleo.
- **Hipótesis:** existe trabajo periódico o recurrente atribuible a uno o más
  componentes opcionales del proceso principal.
- **Métrica objetivo:** CPU acumulada y despertares en reposo, desglosados por
  hilo, pila y componente; incluir asignaciones y actividad de GC.
- **Riesgo:** confundir CPU histórica, instrumental Debug o procesos auxiliares
  con trabajo del manejador de ventanas.
- **Medición requerida:** capturas ETW Release comparables con perfil mínimo y
  completo, manteniendo la misma sesión, hardware y duración.
- **Condición para abordarla:** identificar una pila o actividad recurrente que
  explique una fracción repetible del coste base.

### FUT-002 — Revisar el timer del panel informativo

- **Estado:** pendiente de medición.
- **Observación:** `StatusPanelWindow` mantiene un timer de 250 ms cuando el
  componente está iniciado, incluso mientras la ventana permanece oculta, para
  detectar el borde de pantalla y actualizar su estado.
- **Hipótesis:** una cadencia adaptativa o un modo sin detección de borde puede
  reducir despertares en reposo sin perjudicar el uso mediante hotkey.
- **Métrica objetivo:** CPU, despertares y latencia desde que el puntero alcanza
  el borde hasta que aparece el panel.
- **Riesgo:** introducir retraso perceptible, perder detecciones o empeorar la
  compatibilidad con juegos, múltiples monitores y DPI.
- **Medición requerida:** comparar el comportamiento actual con variantes
  instrumentadas; no sustituirlo por un hook global sin medir su coste.
- **Condición para abordarla:** demostrar que el timer contribuye de forma
  material al reposo en el perfil afectado.

### FUT-003 — Reducir el coste de memoria del fondo

- **Estado:** pendiente de medición.
- **Observación:** `WallpaperSurface` prepara un bitmap de 32 bits con las
  dimensiones completas del escritorio virtual. Su estimación es
  `ancho × alto × 4` bytes: unos 31,6 MiB para 3840×2160 y 63,3 MiB para
  7680×2160, antes de posibles copias del sistema gráfico.
- **Hipótesis:** evitar superficies que cubran huecos del escritorio virtual,
  liberar recursos cuando la función esté deshabilitada o cambiar la estrategia
  por monitor podría reducir memoria en configuraciones multimonitor.
- **Métrica objetivo:** private bytes, working set, objetos GDI y tiempo de
  aplicación/repintado del fondo.
- **Riesgo:** aumentar CPU de pintado, producir parpadeos o romper geometrías
  con coordenadas negativas y DPI mixto.
- **Medición requerida:** fondo deshabilitado, monitor único y varios monitores,
  usando las mismas imágenes y resoluciones.
- **Condición para abordarla:** confirmar que el bitmap explica una parte
  relevante y recuperable de la memoria del proceso.

### FUT-004 — Ciclo de vida de componentes opcionales

- **Estado:** pendiente de diseño y medición.
- **Observación:** la TUI, el bridge de notificaciones y la API local amplían el
  coste de sesión aunque no formen parte del manejo básico de ventanas.
- **Hipótesis:** inicio bajo demanda y terminación limpia de componentes
  deshabilitados o inactivos pueden acercar el reposo al coste del núcleo.
- **Métrica objetivo:** procesos, memoria privada, working set, handles, hilos,
  tiempo de arranque y latencia de primera apertura.
- **Riesgo:** perder eventos mientras un componente está detenido, aumentar la
  latencia inicial o complicar el cierre y la recuperación.
- **Medición requerida:** matriz Release por componente y perfiles mínimo y
  completo, verificando además que cada opción TOML tenga efecto observable.
- **Condición para abordarla:** demostrar un ahorro repetible sin degradar una
  función habilitada ni iniciar Explorer como dependencia implícita.

### FUT-005 — Procesos periódicos de bandeja y TUI

- **Estado:** pendiente de medición.
- **Observación:** los elementos dinámicos pueden ejecutar comandos en timers;
  el ejemplo de batería inicia PowerShell cada cinco segundos. Los widgets TUI
  también admiten refrescos programados.
- **Hipótesis:** eventos, intervalos mayores, agrupación de consultas o refresco
  al mostrar la superficie pueden evitar creación recurrente de procesos.
- **Métrica objetivo:** procesos creados por hora, CPU, I/O, memoria máxima y
  antigüedad del dato mostrado.
- **Riesgo:** entregar información obsoleta o convertir un mecanismo simple y
  configurable en un servicio auxiliar permanente.
- **Medición requerida:** elemento estático frente a comando periódico, con la
  superficie abierta y cerrada y con comandos rápidos y lentos.
- **Condición para abordarla:** observar un coste relevante en una configuración
  real, no solamente en el archivo de ejemplo.

### FUT-006 — Catálogo de aplicaciones incremental

- **Estado:** pendiente de medición.
- **Observación:** `WindowsApplicationCatalog` recorre recursivamente los menús
  Inicio y enumera `shell:AppsFolder` mediante COM en cada refresco completo.
- **Hipótesis:** conservar un snapshot validado o actualizar solo las rutas
  afectadas podría reducir picos de inicio y refresco.
- **Métrica objetivo:** duración, asignaciones, I/O y frecuencia de cada
  reconstrucción del catálogo.
- **Riesgo:** resultados obsoletos, pérdida de aplicaciones empaquetadas o
  complejidad de invalidación superior al ahorro.
- **Medición requerida:** catálogo frío y caliente, ráfagas del
  `FileSystemWatcher`, accesos denegados y cambios de aplicaciones MSIX.
- **Condición para abordarla:** demostrar que las reconstrucciones completas
  afectan de manera repetible el arranque o la interacción.

### FUT-007 — Reutilizar snapshots de ventanas dentro de una operación

- **Estado:** pendiente de medición.
- **Observación:** distintas acciones de workspaces, selector y ventanas
  minimizadas pueden solicitar `Refresh()` y volver a consultar propiedades de
  los mismos HWND mediante `EnumWindows`.
- **Hipótesis:** compartir un snapshot únicamente durante una operación del
  message loop puede eliminar trabajo duplicado sin introducir una caché de
  larga duración.
- **Métrica objetivo:** enumeraciones y llamadas Win32 por acción, latencia de
  hotkeys y P95 bajo `TenchyShellStress`.
- **Riesgo:** operar sobre HWND destruidos, reutilizados o cuyo título,
  visibilidad o propietario cambió durante la acción.
- **Medición requerida:** instrumentar conteos y duración en cambios de
  workspace, Alt+Tab, minimizar, restaurar y cerrar ventanas.
- **Condición para abordarla:** confirmar enumeraciones duplicadas dentro de una
  misma acción y mantener validación final del HWND antes de mutarlo.

### FUT-008 — Perfil mínimo y hardware antiguo

- **Estado:** pendiente de definición después del hito de estabilidad.
- **Observación:** porcentajes normalizados por todos los procesadores lógicos
  pueden ocultar un coste fijo significativo para equipos de pocos núcleos.
- **Hipótesis:** un perfil mínimo explícito permitiría conservar solo el núcleo
  de manejo de ventanas y habilitar el resto por configuración.
- **Métrica objetivo:** memoria privada, working set, CPU por núcleo, latencia de
  hotkeys y consumo en reposo en hardware representativo de 2–4 núcleos.
- **Riesgo:** fragmentar configuraciones o llamar "mínimo" a un perfil que no
  mantenga una sesión diaria utilizable.
- **Medición requerida:** ejecutar el mismo protocolo Release en hardware
  antiguo real o una VM con CPU y memoria limitadas, informando tanto el núcleo
  como la sesión completa.
- **Condición para abordarla:** definir un presupuesto reproducible después de
  reunir la línea base del flujo real actual.

### FUT-009 — Sustituir el observador PowerShell/CIM

- **Estado:** condicionado a la línea base v3.
- **Observación:** el recolector externo consulta CIM y procesos desde
  PowerShell; el esquema 3 mide su CPU, memoria y duración de muestreo fuera de
  los agregados del producto.
- **Hipótesis:** un recolector Win32 podría reducir la perturbación si CIM ocupa
  una fracción material del intervalo o altera repetidamente las colas.
- **Métrica objetivo:** CPU y memoria del observador, mediana/P95 de duración de
  muestreo y cambios en las métricas de `Core` y `ShellSession`.
- **Riesgo:** añadir interoperabilidad y mantenimiento sin una mejora material.
- **Medición requerida:** comparar ambos recolectores en el mismo lote, sesión
  y cadencia, manteniendo idénticas clasificación y validaciones.
- **Condición para abordarla:** evidencia repetida de perturbación significativa;
  durante el hito 0.8.7 se mantiene PowerShell/CIM.

## Flujo de promoción

1. Mantener la entrada aquí mientras sea una observación o hipótesis.
2. Seleccionarla para un hito solo después de contar con una línea base válida.
3. Implementar un cambio acotado y repetir exactamente el escenario previo.
4. Rechazar o revertir la propuesta si no mejora repetidamente su métrica o si
   perjudica estabilidad, P95, recuperación o compatibilidad.
5. Si se acepta, trasladar el resultado a `OPTIMIZATIONS.md` con evidencia
   antes/después y conservar aquí solo una referencia histórica.
