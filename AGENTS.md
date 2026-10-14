# AGENTS.md â€” GuÃ­a del proyecto TenchyShell

> El cÃ³digo, la soluciÃ³n, los ensamblados y las rutas actuales se llaman
> `TenchyShell`. La compatibilidad con datos heredados de `MinimalShell` debe
> mantenerse mediante una migraciÃ³n idempotente y no destructiva.

## PropÃ³sito

TenchyShell es un shell para Windows 11 Pro, liviano, optimizado, modular y
orientado al teclado. Su filosofÃ­a es ofrecer una sesiÃ³n diaria plenamente
usable sin depender de `explorer.exe` como shell o interfaz principal, mantener
compatibilidad con aplicaciones Win32 y usar `explorer.exe` exclusivamente como
fallback explÃ­cito de recuperaciÃ³n.

No es un proyecto para recrear Windows Explorer, una taskbar completa ni un
escritorio convencional. Cada componente debe justificar su coste en memoria,
CPU, latencia, complejidad y mantenimiento. Se delega en APIs y herramientas de
Windows cuando no exista una ventaja clara y medible de implementarlo aquÃ­.

La independencia de Explorer es un requisito funcional: las funciones propias
no deben iniciarlo, requerir su superficie de escritorio ni depender de su
bandeja privada. Esta independencia no autoriza a eliminar mecanismos de
recuperaciÃ³n ni a sustituir componentes de seguridad de Windows.

## Prioridades

Al tomar decisiones tÃ©cnicas, respetar este orden:

1. Estabilidad.
2. Compatibilidad con Windows 11 Pro.
3. Bajo consumo de recursos y respuesta predecible.
4. Simplicidad y dependencias mÃ­nimas.
5. Experiencia orientada al teclado.
6. Modularidad.
7. PersonalizaciÃ³n.
8. Apariencia.

Evitar la optimizaciÃ³n prematura y la sobrearquitectura. Las optimizaciones se
aceptan con una hipÃ³tesis y mediciÃ³n reproducible; no se aÃ±aden timers, polling,
cachÃ©s ni procesos auxiliares sin justificar su coste.

## Stack y lÃ­mites tÃ©cnicos

- Lenguaje principal: C#.
- Runtime objetivo: .NET 10.
- Plataforma objetivo: Windows 11 Pro.
- Interoperabilidad nativa: Win32 mediante P/Invoke.
- UI: inicialmente ninguna o la mÃ­nima imprescindible; usar WPF solo para componentes sencillos posteriores.
- DWM debe permanecer como compositor de ventanas.

La interoperabilidad Win32 debe estar encapsulada en el proyecto o capa correspondiente. El resto del cÃ³digo no debe repartir llamadas P/Invoke directamente por toda la aplicaciÃ³n.

APIs previstas, segÃºn la necesidad concreta: `RegisterHotKey`, `EnumWindows`, `GetForegroundWindow`, `SetForegroundWindow`, `SetWindowPos`, `MoveWindow`, `ShowWindow`, `GetWindowRect`, `MonitorFromWindow`, `GetMonitorInfo`, `PostMessage` y `SendMessage`.

No asumir que una API o configuraciÃ³n funciona igual para todas las ventanas. Investigar y probar especialmente aplicaciones UWP/WinUI, juegos, ventanas `always-on-top`, mÃºltiples monitores y ventanas especiales.

## Arquitectura esperada

La estructura preferida es:

```text
TenchyShell.slnx
â”œâ”€â”€ src/
â”‚   â”œâ”€â”€ TenchyShell.App          # Entry point y ciclo de vida
â”‚   â”œâ”€â”€ TenchyShell.Core         # ConfiguraciÃ³n, comandos, procesos y modelos
â”‚   â”œâ”€â”€ TenchyShell.Win32        # NativeMethods, ventanas, monitores, hotkeys y eventos
â”‚   â”œâ”€â”€ TenchyShell.Workspaces   # Workspaces, tracking y foco
â”‚   â””â”€â”€ TenchyShell.UI           # UI futura, reservada inicialmente
â””â”€â”€ tests/
    â”œâ”€â”€ TenchyShell.Core.Tests
    â””â”€â”€ TenchyShell.Workspaces.Tests
```

Mantener dependencias unidireccionales y responsabilidades claras. La lÃ³gica de dominio debe poder probarse sin depender de una sesiÃ³n grÃ¡fica de Windows cuando sea posible.

Los nombres de proyectos, ensamblados y namespaces usan `TenchyShell`. El nombre
`MinimalShell` solo puede permanecer en cÃ³digo de migraciÃ³n, pruebas de
compatibilidad o documentaciÃ³n histÃ³rica claramente identificada.

## Base funcional y estado actual

La base del MVP 0.1 ya incorpora y debe conservar:

- ciclo de vida y message loop de Win32;
- registro de hotkeys globales;
- lanzamiento de procesos y aplicaciones configuradas;
- integraciÃ³n con PowerToys Command Palette;
- integraciÃ³n con el terminal configurado;
- apertura de Yazi mediante el terminal;
- detecciÃ³n de la ventana activa y comando para cerrarla;
- logout, shutdown y reboot;
- inicio manual de `explorer.exe` como recuperaciÃ³n;
- manejo de errores globales y logs.

Hotkeys iniciales propuestos:

```text
Ctrl + Alt + Space      Launcher mÃ­nimo de TenchyShell
Win + Enter             Terminal
Win + E                 Terminal + Yazi
Win + B                 Navegador
Win + Q                 Cerrar ventana activa
Win + 1..9              Workspace 1..9 (a partir del MVP 0.2)
Ctrl + Alt + Shift + E  RecuperaciÃ³n: iniciar explorer.exe
```

Los hotkeys son configurables; no hardcodear aplicaciones ni combinaciones en
varios lugares. Las combinaciones reservadas por Windows o tomadas por otras
aplicaciones deben fallar de forma observable sin terminar el shell.

### Capacidades incorporadas posteriores

- Workspaces, gestiÃ³n bÃ¡sica de ventanas y selector visible de ventanas del
  workspace actual.
- Layout por zonas con hotkeys, overlay de arrastre, mÃºltiples monitores y DPI.
- Panel informativo auto-ocultable y una bandeja/dock Win32 propia,
  independiente de Explorer.
- Elementos estÃ¡ticos y dinÃ¡micos de bandeja mediante scripts configurados y
  acciones declaradas; los iconos privados de aplicaciones de Explorer no se
  capturan automÃ¡ticamente.
- Centro de redes propio para estado de interfaces, Wi-Fi y Ethernet, sin
  guardar credenciales; las operaciones avanzadas se delegan en Windows.
- Selector de fondos mediante una superficie propia detrÃ¡s de las ventanas, con
  persistencia de la Ãºltima ruta vÃ¡lida y sin modificar imÃ¡genes del usuario.

No duplicar Yazi con un administrador de archivos propio ni convertir el
launcher, dock o bandeja en reemplazos generales de Explorer. Tampoco
implementar un menÃº Start, notificaciones, controles de Wi-Fi/Bluetooth/audio
o una taskbar completa fuera de los hitos expresamente definidos.

## EvoluciÃ³n prevista

### MVP 0.2 â€” Workspaces

Usar un `WorkspaceManager`, `WindowTracker` y `FocusManager`. La implementaciÃ³n inicial puede asociar colecciones de `HWND` a cada workspace. Al cambiar de workspace, ocultar las ventanas del actual, mostrar las del destino y restaurar el foco. DespuÃ©s aÃ±adir `Win + Shift + 1..9` para mover la ventana activa.

### MVP 0.3 â€” Window manager

Evaluar operaciones como mover o redimensionar ventanas, navegaciÃ³n de foco y traslado entre monitores. Inspirarse en Niri, i3, Sway, GlazeWM o komorebi sin copiar su diseÃ±o automÃ¡ticamente. Resolver primero compatibilidad y comportamiento observable.

### MVP 0.7 â€” Funciones de sesiÃ³n y consolidaciÃ³n

- Mantener y validar selector de ventanas, bandeja propia, centro de redes y
  fondos, siempre sin dependencia de Explorer.
- Implementar el visualizador y selector de idioma de teclado desde el dock;
  solo muestra y activa mÃ©todos de entrada ya habilitados por Windows, sin
  instalar ni administrar paquetes de idioma.
- Medir la sesiÃ³n completa contra un escenario Explorer equivalente y optimizar
  Ãºnicamente segÃºn resultados reproducibles.
- Mantener la identidad `TenchyShell` y la migraciÃ³n idempotente y no destructiva
  desde configuraciÃ³n, logs y estado heredados de `MinimalShell`.

## Roadmap de hitos posteriores

Los hitos posteriores al MVP 0.1 estÃ¡n definidos en
[`mds/mvp-0.5.md`](mds/mvp-0.5.md), [`mds/mvp-0.6.md`](mds/mvp-0.6.md) y
[`mds/mvp-0.7.md`](mds/mvp-0.7.md). El hito actual de estabilizaciÃ³n 0.8.7 y su
benchmark externo v3 estÃ¡n definidos en [`mds/mvp-0.8.md`](mds/mvp-0.8.md).
Antes de implementar una funcionalidad,
revisar el documento correspondiente y respetar su orden, dependencias,
entregables y pruebas.

### Regla de versionado por hito

El nÃºmero de versiÃ³n del programa debe coincidir con el hito en curso. Al
iniciar o cambiar de hito, actualizar `ProductInfo.Version` en
`src/TenchyShell.Core/ProductInfo.cs` y todas las pruebas o metadatos que lo
verifiquen; las superficies visibles (splash, diagnÃ³sticos y similares) deben
leer esa fuente central y no mantener versiones hardcodeadas. Antes de cerrar
un cambio, comprobar que la aplicaciÃ³n y la suite muestran la versiÃ³n del
hito actual y que no quedan referencias ejecutables a la versiÃ³n anterior.

El hito 0.8.8 aÃ±ade ajustes de usabilidad al menÃº del dock: conserva la
confirmaciÃ³n doble de las acciones de sesiÃ³n, reserva su espacio visual para
evitar desplazamientos de las filas y aumenta en un 15% el tamaÃ±o de los
elementos seleccionables. Las mÃ©tricas de dibujo y hit-testing deben
permanecer compartidas y la validaciÃ³n manual en Windows 11 debe cubrir mouse,
teclado, DPI y las tres acciones de sesiÃ³n.

El hito 0.8.9 aÃ±ade un splash de inicio nativo y temporal para el arranque
interactivo normal. Debe dibujarse con Win32/GDI, reportar las fases reales de
preparaciÃ³n mediante un contrato testeable de Core, integrarse con el
`MessageLoopHost` sin crear otro loop y ocultarse/liberarse antes de la sesiÃ³n
normal. No se muestra en `--check`, `--launch`, `--session`, `--tui`,
diagnÃ³sticos empaquetados ni benchmarks automatizados; un fallo de creaciÃ³n o
de una fase obligatoria debe cerrar la superficie, dejar el error en consola y
logs y terminar con cÃ³digo distinto de cero.

Para capturas manuales, `--keep-splash` puede mantener la superficie visible
cuando la sesiÃ³n ya estÃ¡ preparada; un clic sobre ella la cierra sin detener
el shell.

La identidad visual del splash usa el arte estÃ¡tico aprobado por el usuario,
copiado como recurso del ensamblado y renderizado con GDI+; el dibujo vectorial
GDI se conserva como fallback si el recurso no estÃ¡ disponible.

El hito 0.8.11 optimiza la publicaciÃ³n selectiva de la API local v1: un cambio
solo construye snapshot y NDJSON si existen suscriptores del tÃ³pico afectado,
y el payload se comparte entre ellos. Cada cliente mantiene una cola FIFO de
16 mensajes y un Ãºnico escritor; ante backpressure se desconecta de forma
observable sin bloquear el message loop. La telemetrÃ­a agregada de esta ruta
solo se registra cuando `[benchmark].enabled = true` y no incluye payloads ni
datos de sesiÃ³n.

El hito 0.8.12 comparte el catÃ¡logo de aplicaciones entre el launcher y la API
local cuando ambos estÃ¡n habilitados. La decisiÃ³n se valida con mediciones en
Release y un umbral mÃ­nimo del 10 % en el P95 de carga o refresco; si no existe
una mejora material, se conserva la arquitectura anterior y se documenta la
mediciÃ³n. Los logs normales muestran resÃºmenes seguros y el benchmark opt-in
registra las mÃ©tricas detalladas de enumeraciÃ³n.

El hito 0.8.15 estÃ¡ cerrado condicionalmente por aceptaciÃ³n del usuario: se
completaron la instrumentaciÃ³n opt-in, las pruebas automatizadas disponibles y
la observaciÃ³n de estabilidad en una sesiÃ³n real. La instrumentaciÃ³n comÃºn
sÃ³lo existe bajo `[benchmark].enabled = true` y persiste agregados seguros, sin
comandos, argumentos, salida ni datos de sesiÃ³n. Por falta temporal de un
entorno controlado, quedan como revalidaciÃ³n diferida el benchmark comparativo
de Windows 11 Release y la comprobaciÃ³n explÃ­cita de ausencia de hijos tras
cancelaciÃ³n. El protocolo mide, durante 30 minutos y tres repeticiones,
bandeja/TUI visibles y cerradas u ocultas con los intervalos corto y normal ya
configurados, comparando baseline y variante. No se declararÃ¡ mejora ni se
modificarÃ¡n visibilidad, TOML o intervalos hasta demostrar una reducciÃ³n
repetible de al menos un 10 % en procesos/hora, CPU o I/O, sin pÃ©rdida de
frescura ni procesos hijos vivos.

`AGENTS.md` forma parte del repositorio y debe mantenerse actualizado cuando
cambie el alcance, la arquitectura o las reglas de desarrollo. `mds/` conserva
su carÃ¡cter de documentaciÃ³n local de planificaciÃ³n y permanece excluido por
`.gitignore`.

El hito 0.8.17 mantiene los hooks globales de layout por defecto y aÃ±ade sÃ³lo
instrumentaciÃ³n agregada opt-in para atribuir su coste. No se registran
coordenadas, teclas, HWND, tÃ­tulos ni datos de sesiÃ³n. Una variante de
rendimiento requiere un baseline ETW reproducible en Release y una mejora de
al menos el 10 % sin degradar arrastre, foco, DPI ni cancelaciÃ³n.

El hito 0.8.18 instrumenta de forma agregada y opt-in el renderizado e iconos
de la bandeja. La ruta normal conserva `LoadImage`/`DestroyIcon` por pintura;
no se introduce cachÃ© hasta demostrar en Release una reducciÃ³n repetible de al
menos el 10 % con handles GDI estables y sin regresiones de navegaciÃ³n, estados
de error o liberaciÃ³n de recursos.

El hito 0.8.18 queda cerrado parcialmente por aceptaciÃ³n del usuario
(2026-09-22): se acepta la instrumentaciÃ³n opt-in, la compilaciÃ³n Release y la
suite automatizada; el benchmark comparativo Release y la validaciÃ³n manual en
Windows 11 quedan diferidos. Este cierre no afirma una mejora de rendimiento
ni autoriza una cachÃ©.

El hito 0.8.19 â€”centro de notificacionesâ€” quedÃ³ cerrado parcialmente por
aceptaciÃ³n del usuario (2026-09-22): la prueba manual confirmÃ³ que el bridge
recibiÃ³ una notificaciÃ³n nueva de Auto HDR y TenchyShell la mostrÃ³. Sigue
pendiente el benchmark comparativo Release de cinco repeticiones del flujo
bridge MSIX, named pipe y emisor MSIX, la validaciÃ³n restante del
historial/tarjetas, la ruta con notificaciones deshabilitadas y la repeticiÃ³n
de las pruebas afectadas. `dotnet test TenchyShell.slnx -c Release
--no-restore` compilÃ³, pero terminÃ³ con 247 pruebas aprobadas y 3 fallidas por
`UnauthorizedAccessException` en pruebas named pipe de `LocalApiServerTests`;
no se considera la suite verde. Una repeticiÃ³n fuera del sandbox dejÃ³ pasar
dos de esas pruebas, pero `FifthClientReceivesConnectionLimitResponse` fallÃ³
por `TimeoutException` y volviÃ³ a agotar cinco segundos al repetirse aislada;
queda pendiente investigar las pruebas afectadas en un entorno controlado. No
se declara mejora ni se introduce cachÃ©.
El tamaÃ±o de los bytes presentados al decodificador de iconos no representa
bytes de I/O fÃ­sico. El hito 0.8.20 â€”telemetrÃ­a live de benchmarkâ€” quedÃ³
cerrado parcialmente por aceptaciÃ³n del usuario (2026-09-30); la versiÃ³n del
programa al cerrar ese hito era `0.8.20`. Una sesiÃ³n real confirmÃ³ que el benchmark
opt-in operÃ³ y cerrÃ³ limpiamente, sin incidencias del recorder observadas; se
obtuvieron 4952 eventos parseables y una lÃ­nea invÃ¡lida, por lo que no se
afirma completitud. Las mÃ©tricas de `FileLogger` no acreditan el coste propio
de la telemetrÃ­a live. Quedan pendientes investigar la lÃ­nea invÃ¡lida, medir en
Release aperturas, serializaciÃ³n, CPU, I/O real y latencia del recorder,
evaluar batching con flush/cierre seguros, probar disco fallido, retenciÃ³n,
cuota y resumidores, y demostrar la separaciÃ³n de las mÃ©tricas oficiales. No
se declara mejora ni se autoriza optimizaciÃ³n.

El hito en curso 0.8.21 â€”refresco del catÃ¡logo de aplicacionesâ€” mantiene el
scheduler, el filtrado, la enumeraciÃ³n y el contrato de la API local. Conserva
el alcance existente: el watcher sÃ³lo se crea con la API
habilitada y el catÃ¡logo se comparte con el launcher cuando la bÃºsqueda de
aplicaciones estÃ¡ habilitada; con la API deshabilitada el launcher no recibe
refrescos de este watcher. No se aÃ±aden seÃ±ales nuevas. Su primera
entrega conecta el watcher al recorder existente sÃ³lo bajo
`[benchmark].enabled = true`, mediante `application_catalog_watcher_metrics`,
y conserva `application_catalog_refresh`. Registra exclusivamente contadores
agregados por tipo de evento, errores, solicitudes agrupadas y refrescos, y
tiempos de duraciÃ³n y demora desde la primera y Ãºltima seÃ±al del grupo. El
grupo se captura al iniciar el refresco; las seÃ±ales recibidas durante Ã©l
pertenecen al siguiente. El cierre emite los pendientes y distingue los grupos
sin refresco completado. Con benchmark deshabilitado no se mantienen
contadores, mediciones ni payloads de esta instrumentaciÃ³n; no se registran
rutas, nombres de aplicaciones ni contenido de eventos.
El resultado `refresh_completed` identifica un refresco completado;
`refresh_failed`, un intento que terminÃ³ con error; y `shutdown_pending`, un
grupo pendiente al cerrar que no iniciÃ³ un refresco y no tiene tiempos de
finalizaciÃ³n. La duraciÃ³n del watcher incluye refresco y publicaciÃ³n de
`state.RefreshApplications`; `application_catalog_refresh` mide enumeraciÃ³n.

La validaciÃ³n de 0.8.21 usa exclusivamente la sesiÃ³n diaria real de Windows en
Release, con benchmark activado y desactivado, sin VM, cargas generadas,
simulaciones ni pruebas sintÃ©ticas. El objetivo de visibilidad es cinco
segundos, sujeto a evidencia temporal suficiente en el launcher o cliente
real; los tiempos internos sÃ³lo describen procesamiento del catÃ¡logo. Los
casos no observados permanecen pendientes. Se difieren la comparaciÃ³n de
operaciones reales equivalentes, CPU e I/O fÃ­sico atribuibles, rÃ¡fagas,
accesos denegados, desbordamiento y apagado concurrente no observados, y los
cambios MSIX sin eventos en el menÃº Inicio, para los que el watcher no tiene
seÃ±al dedicada. No se declara mejora ni cierre; debounce y filtros requieren
evidencia posterior de coste material y este hito no fija un umbral porcentual.
La versiÃ³n central del programa para este hito es `0.8.21`.

El hito 0.8.22 - Red y apertura de bandeja - queda cerrado parcialmente por aceptacion del usuario. Se completo la instrumentacion opt-in, la limpieza de alias, el resumen agregado de VPN y el texto multilinea en la bandeja. Quedan pendientes la validacion manual completa de Ethernet, Wi-Fi, VPN, errores WLAN y acciones, y el benchmark comparativo formal. No se declara mejora de rendimiento ni se autoriza cache. La version central del programa para este hito es 0.8.22.

El hito 0.8.23 - primer release publico GitHub - esta en curso. Incluye todo el desarrollo acumulado hasta la fecha, aunque hitos anteriores puedan estar cerrados parcialmente. La publicacion se valida desde un arbol limpio, con suite .NET y Pester en Release, publicacion win-x64, --check, ZIP/checksum e instalador. El benchmark comparativo o la validacion manual pendientes deben quedar indicados en las notas del release sin declarar mejoras no demostradas. La version central del programa para este hito es 0.8.23.

## ConfiguraciÃ³n

Preferir TOML por legibilidad. La configuraciÃ³n futura debe cubrir, como mÃ­nimo, terminal, file manager, launcher, navegador, aplicaciones y hotkeys. Proveer valores por defecto razonables y validar errores de configuraciÃ³n al inicio con mensajes claros y logs.

Ejemplo conceptual:

```toml
[terminal]
command = "wezterm.exe"

[file_manager]
command = "yazi.exe"

[applications]
browser = "..."

[hotkeys]
terminal = "Win+Enter"
files = "Win+E"
launcher = "Ctrl+Alt+Space"
close_window = "Win+Q"
```

El terminal debe poder ser Windows Terminal o WezTerm, sin acoplar la lÃ³gica central a uno concreto. WezTerm es la alternativa configurable objetivo para este proyecto.

## Seguridad, recuperaciÃ³n y pruebas

- Durante el desarrollo no cambiar permanentemente la configuraciÃ³n de Winlogon.
- Probar primero con Explorer disponible y luego, de forma controlada, detenerlo temporalmente.
- Para pruebas agresivas usar una mÃ¡quina virtual o un usuario secundario; nunca depender solo de la sesiÃ³n principal.
- Mantener siempre una ruta de recuperaciÃ³n que pueda iniciar `explorer.exe`.
- Registrar errores y eventos importantes en `%LOCALAPPDATA%\TenchyShell\logs\`.
- Una excepciÃ³n no debe terminar silenciosamente el shell.
- No interferir con `Ctrl + Alt + Delete` ni con mecanismos de seguridad de Windows.
- Antes de usar el shell como reemplazo de Winlogon, demostrar que el MVP mantiene una sesiÃ³n utilizable sin Explorer.

El cambio de `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` queda fuera del MVP y requiere una fase explÃ­cita de diseÃ±o, pruebas y recuperaciÃ³n.

## CoordinaciÃ³n mediante subagentes

El agente principal actÃºa exclusivamente como orquestador y punto de
comunicaciÃ³n: divide el trabajo, lo asigna a subagentes, consolida sus
resultados, resuelve conflictos de alcance y comunica el resultado al usuario.
Debe delegar el trabajo del proyecto, incluida la exploraciÃ³n del repositorio,
la creaciÃ³n y revisiÃ³n de planes, la implementaciÃ³n de hitos, las pruebas, las
validaciones y el anÃ¡lisis.

Como regla de eficiencia de coordinaciÃ³n, los cambios triviales, de alcance
obvio y bajo riesgo â€”por ejemplo, una lÃ­nea de configuraciÃ³n, documentaciÃ³n
puntual o un ajuste de prueba directamente asociadoâ€” pueden realizarse
directamente por el agente principal. Se debe delegar cuando el trabajo sea
complejo, paralelo, de alto riesgo o requiera revisiÃ³n independiente. En todos
los casos se deben preservar los cambios locales, validar el resultado y
comunicarlo al usuario.

Los subagentes asignados son agentes hoja y deben realizar directamente la
exploraciÃ³n, implementaciÃ³n, pruebas o validaciÃ³n que se les haya asignado.
Los agentes hoja no deben delegar nuevamente ni crear subagentes. Esta
excepciÃ³n prevalece sobre la regla general de delegaciÃ³n: una vez asignada una
tarea a un agente hoja, este debe ejecutarla directamente dentro del alcance
indicado.

Cada tarea delegada debe indicar explÃ­citamente su alcance, los archivos
permitidos y que el subagente actÃºa como agente hoja.

Si no hay capacidad disponible para delegar una tarea, el agente principal debe
esperar a que la haya o informar el bloqueo al usuario; no puede sustituir al
subagente y realizar esa tarea directamente.

## GuÃ­a de implementaciÃ³n

Antes de modificar cÃ³digo:

1. Inspeccionar la estructura existente y preservar cambios del usuario.
2. Identificar el proyecto afectado y mantener el cambio acotado.
3. Confirmar el comportamiento esperado en Windows 11 Pro.
4. AÃ±adir o actualizar pruebas para la lÃ³gica que no dependa de la API grÃ¡fica.

Al implementar:

- preferir cÃ³digo claro y pequeÃ±o sobre abstracciones prematuras;
- encapsular procesos, hotkeys y APIs nativas detrÃ¡s de servicios simples;
- hacer explÃ­citos los errores de lanzamiento y configuraciÃ³n;
- evitar dependencias externas si una API de Windows o una herramienta ya instalada resuelve el problema;
- documentar supuestos sobre `HWND`, foco, visibilidad, monitores y permisos;
- no convertir funcionalidades de PowerToys o Yazi en implementaciones propias sin una decisiÃ³n explÃ­cita.
- para cÃ³digo o cambios de rendimiento, medir antes y despuÃ©s con el protocolo
  del hito 0.7.6 y conservar los datos y criterios de comparaciÃ³n.
- al tocar configuraciÃ³n, estado o rutas de usuario, preservar datos existentes,
  no sobrescribir sin confirmaciÃ³n y mantener una migraciÃ³n idempotente.

## Criterios de finalizaciÃ³n

Un cambio estÃ¡ listo cuando compila para .NET 10, las pruebas relevantes pasan, los errores previsibles tienen manejo observable y la funcionalidad no rompe la recuperaciÃ³n de la sesiÃ³n. Para cambios que interactÃºan con ventanas, probar con varias aplicaciones Win32 y, cuando corresponda, con mÃºltiples monitores y ventanas especiales.

La definiciÃ³n de Ã©xito del proyecto sigue siendo:

```text
Windows 11 + DWM + TenchyShell.exe + Terminal/Yazi
funcionando de forma estable, liviana y optimizada sin depender de explorer.exe
como shell principal, con Explorer disponible Ãºnicamente para recuperaciÃ³n.
```
