# AGENTS.md — Guía del proyecto TenchyShell

> El código, la solución, los ensamblados y las rutas actuales se llaman
> `TenchyShell`. La compatibilidad con datos heredados de `MinimalShell` debe
> mantenerse mediante una migración idempotente y no destructiva.

## Propósito

TenchyShell es un shell para Windows 11 Pro, liviano, optimizado, modular y
orientado al teclado. Su filosofía es ofrecer una sesión diaria plenamente
usable sin depender de `explorer.exe` como shell o interfaz principal, mantener
compatibilidad con aplicaciones Win32 y usar `explorer.exe` exclusivamente como
fallback explícito de recuperación.

No es un proyecto para recrear Windows Explorer, una taskbar completa ni un
escritorio convencional. Cada componente debe justificar su coste en memoria,
CPU, latencia, complejidad y mantenimiento. Se delega en APIs y herramientas de
Windows cuando no exista una ventaja clara y medible de implementarlo aquí.

La independencia de Explorer es un requisito funcional: las funciones propias
no deben iniciarlo, requerir su superficie de escritorio ni depender de su
bandeja privada. Esta independencia no autoriza a eliminar mecanismos de
recuperación ni a sustituir componentes de seguridad de Windows.

## Prioridades

Al tomar decisiones técnicas, respetar este orden:

1. Estabilidad.
2. Compatibilidad con Windows 11 Pro.
3. Bajo consumo de recursos y respuesta predecible.
4. Simplicidad y dependencias mínimas.
5. Experiencia orientada al teclado.
6. Modularidad.
7. Personalización.
8. Apariencia.

Evitar la optimización prematura y la sobrearquitectura. Las optimizaciones se
aceptan con una hipótesis y medición reproducible; no se añaden timers, polling,
cachés ni procesos auxiliares sin justificar su coste.

## Stack y límites técnicos

- Lenguaje principal: C#.
- Runtime objetivo: .NET 10.
- Plataforma objetivo: Windows 11 Pro.
- Interoperabilidad nativa: Win32 mediante P/Invoke.
- UI: inicialmente ninguna o la mínima imprescindible; usar WPF solo para componentes sencillos posteriores.
- DWM debe permanecer como compositor de ventanas.

La interoperabilidad Win32 debe estar encapsulada en el proyecto o capa correspondiente. El resto del código no debe repartir llamadas P/Invoke directamente por toda la aplicación.

APIs previstas, según la necesidad concreta: `RegisterHotKey`, `EnumWindows`, `GetForegroundWindow`, `SetForegroundWindow`, `SetWindowPos`, `MoveWindow`, `ShowWindow`, `GetWindowRect`, `MonitorFromWindow`, `GetMonitorInfo`, `PostMessage` y `SendMessage`.

No asumir que una API o configuración funciona igual para todas las ventanas. Investigar y probar especialmente aplicaciones UWP/WinUI, juegos, ventanas `always-on-top`, múltiples monitores y ventanas especiales.

## Arquitectura esperada

La estructura preferida es:

```text
TenchyShell.slnx
├── src/
│   ├── TenchyShell.App          # Entry point y ciclo de vida
│   ├── TenchyShell.Core         # Configuración, comandos, procesos y modelos
│   ├── TenchyShell.Win32        # NativeMethods, ventanas, monitores, hotkeys y eventos
│   ├── TenchyShell.Workspaces   # Workspaces, tracking y foco
│   └── TenchyShell.UI           # UI futura, reservada inicialmente
└── tests/
    ├── TenchyShell.Core.Tests
    └── TenchyShell.Workspaces.Tests
```

Mantener dependencias unidireccionales y responsabilidades claras. La lógica de dominio debe poder probarse sin depender de una sesión gráfica de Windows cuando sea posible.

Los nombres de proyectos, ensamblados y namespaces usan `TenchyShell`. El nombre
`MinimalShell` solo puede permanecer en código de migración, pruebas de
compatibilidad o documentación histórica claramente identificada.

## Base funcional y estado actual

La base del MVP 0.1 ya incorpora y debe conservar:

- ciclo de vida y message loop de Win32;
- registro de hotkeys globales;
- lanzamiento de procesos y aplicaciones configuradas;
- integración con PowerToys Command Palette;
- integración con el terminal configurado;
- apertura de Yazi mediante el terminal;
- detección de la ventana activa y comando para cerrarla;
- logout, shutdown y reboot;
- inicio manual de `explorer.exe` como recuperación;
- manejo de errores globales y logs.

Hotkeys iniciales propuestos:

```text
Ctrl + Alt + Space      Launcher mínimo de TenchyShell
Win + Enter             Terminal
Win + E                 Terminal + Yazi
Win + B                 Navegador
Win + Q                 Cerrar ventana activa
Win + 1..9              Workspace 1..9 (a partir del MVP 0.2)
Ctrl + Alt + Shift + E  Recuperación: iniciar explorer.exe
```

Los hotkeys son configurables; no hardcodear aplicaciones ni combinaciones en
varios lugares. Las combinaciones reservadas por Windows o tomadas por otras
aplicaciones deben fallar de forma observable sin terminar el shell.

### Capacidades incorporadas posteriores

- Workspaces, gestión básica de ventanas y selector visible de ventanas del
  workspace actual.
- Layout por zonas con hotkeys, overlay de arrastre, múltiples monitores y DPI.
- Panel informativo auto-ocultable y una bandeja/dock Win32 propia,
  independiente de Explorer.
- Elementos estáticos y dinámicos de bandeja mediante scripts configurados y
  acciones declaradas; los iconos privados de aplicaciones de Explorer no se
  capturan automáticamente.
- Centro de redes propio para estado de interfaces, Wi-Fi y Ethernet, sin
  guardar credenciales; las operaciones avanzadas se delegan en Windows.
- Selector de fondos mediante una superficie propia detrás de las ventanas, con
  persistencia de la última ruta válida y sin modificar imágenes del usuario.

No duplicar Yazi con un administrador de archivos propio ni convertir el
launcher, dock o bandeja en reemplazos generales de Explorer. Tampoco
implementar un menú Start, notificaciones, controles de Wi-Fi/Bluetooth/audio
o una taskbar completa fuera de los hitos expresamente definidos.

## Evolución prevista

### MVP 0.2 — Workspaces

Usar un `WorkspaceManager`, `WindowTracker` y `FocusManager`. La implementación inicial puede asociar colecciones de `HWND` a cada workspace. Al cambiar de workspace, ocultar las ventanas del actual, mostrar las del destino y restaurar el foco. Después añadir `Win + Shift + 1..9` para mover la ventana activa.

### MVP 0.3 — Window manager

Evaluar operaciones como mover o redimensionar ventanas, navegación de foco y traslado entre monitores. Inspirarse en Niri, i3, Sway, GlazeWM o komorebi sin copiar su diseño automáticamente. Resolver primero compatibilidad y comportamiento observable.

### MVP 0.7 — Funciones de sesión y consolidación

- Mantener y validar selector de ventanas, bandeja propia, centro de redes y
  fondos, siempre sin dependencia de Explorer.
- Implementar el visualizador y selector de idioma de teclado desde el dock;
  solo muestra y activa métodos de entrada ya habilitados por Windows, sin
  instalar ni administrar paquetes de idioma.
- Medir la sesión completa contra un escenario Explorer equivalente y optimizar
  únicamente según resultados reproducibles.
- Mantener la identidad `TenchyShell` y la migración idempotente y no destructiva
  desde configuración, logs y estado heredados de `MinimalShell`.

## Roadmap del MVP 0.5

Los hitos posteriores al MVP 0.1 están definidos en
[`mds/mvp-0.5.md`](mds/mvp-0.5.md), [`mds/mvp-0.6.md`](mds/mvp-0.6.md) y
[`mds/mvp-0.7.md`](mds/mvp-0.7.md). Antes de implementar una funcionalidad,
revisar el documento correspondiente y respetar su orden, dependencias,
entregables y pruebas.

`AGENTS.md` forma parte del repositorio y debe mantenerse actualizado cuando
cambie el alcance, la arquitectura o las reglas de desarrollo. `mds/` conserva
su carácter de documentación local de planificación y permanece excluido por
`.gitignore`.

## Configuración

Preferir TOML por legibilidad. La configuración futura debe cubrir, como mínimo, terminal, file manager, launcher, navegador, aplicaciones y hotkeys. Proveer valores por defecto razonables y validar errores de configuración al inicio con mensajes claros y logs.

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

El terminal debe poder ser Windows Terminal o WezTerm, sin acoplar la lógica central a uno concreto. WezTerm es la alternativa configurable objetivo para este proyecto.

## Seguridad, recuperación y pruebas

- Durante el desarrollo no cambiar permanentemente la configuración de Winlogon.
- Probar primero con Explorer disponible y luego, de forma controlada, detenerlo temporalmente.
- Para pruebas agresivas usar una máquina virtual o un usuario secundario; nunca depender solo de la sesión principal.
- Mantener siempre una ruta de recuperación que pueda iniciar `explorer.exe`.
- Registrar errores y eventos importantes en `%LOCALAPPDATA%\TenchyShell\logs\`.
- Una excepción no debe terminar silenciosamente el shell.
- No interferir con `Ctrl + Alt + Delete` ni con mecanismos de seguridad de Windows.
- Antes de usar el shell como reemplazo de Winlogon, demostrar que el MVP mantiene una sesión utilizable sin Explorer.

El cambio de `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` queda fuera del MVP y requiere una fase explícita de diseño, pruebas y recuperación.

## Guía de implementación

Antes de modificar código:

1. Inspeccionar la estructura existente y preservar cambios del usuario.
2. Identificar el proyecto afectado y mantener el cambio acotado.
3. Confirmar el comportamiento esperado en Windows 11 Pro.
4. Añadir o actualizar pruebas para la lógica que no dependa de la API gráfica.

Al implementar:

- preferir código claro y pequeño sobre abstracciones prematuras;
- encapsular procesos, hotkeys y APIs nativas detrás de servicios simples;
- hacer explícitos los errores de lanzamiento y configuración;
- evitar dependencias externas si una API de Windows o una herramienta ya instalada resuelve el problema;
- documentar supuestos sobre `HWND`, foco, visibilidad, monitores y permisos;
- no convertir funcionalidades de PowerToys o Yazi en implementaciones propias sin una decisión explícita.
- para código o cambios de rendimiento, medir antes y después con el protocolo
  del hito 0.7.6 y conservar los datos y criterios de comparación.
- al tocar configuración, estado o rutas de usuario, preservar datos existentes,
  no sobrescribir sin confirmación y mantener una migración idempotente.

## Criterios de finalización

Un cambio está listo cuando compila para .NET 10, las pruebas relevantes pasan, los errores previsibles tienen manejo observable y la funcionalidad no rompe la recuperación de la sesión. Para cambios que interactúan con ventanas, probar con varias aplicaciones Win32 y, cuando corresponda, con múltiples monitores y ventanas especiales.

La definición de éxito del proyecto sigue siendo:

```text
Windows 11 + DWM + TenchyShell.exe + Terminal/Yazi
funcionando de forma estable, liviana y optimizada sin depender de explorer.exe
como shell principal, con Explorer disponible únicamente para recuperación.
```
