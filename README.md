# TenchyShell

Shell minimalista para Windows 11 Pro, orientado al teclado y diseñado para reducir la dependencia cotidiana de `explorer.exe` sin reemplazar el compositor DWM ni la compatibilidad con aplicaciones Win32.

TenchyShell fue desarrollado con asistencia de OpenAI Codex, manteniendo una arquitectura pequeña, nativa y enfocada en la recuperación segura de la sesión.

## Estado actual

El MVP 0.7.6 consolida el producto bajo el nombre TenchyShell. Incluye el
message loop Win32, recuperación de Explorer, launcher, workspaces, gestión y
layout de ventanas, panel, bandeja propia, red, fondos e idioma de teclado. La
aplicación mantiene una única instancia compatible con el mutex heredado y las
consultas de red del dock se ejecutan fuera del message loop.

## Requisitos

- Windows 11 Pro.
- .NET SDK 10.0 o posterior compatible con `net10.0`.
- PowerShell.
- WezTerm y Yazi para las pruebas de integración.

## Instalación

Cada tag `vX.Y.Z` genera un GitHub Release `win-x64` auto-contenido: no requiere instalar el SDK ni el runtime de .NET. Descarga el ZIP, su archivo `.sha256` y el bootstrapper; después valida e instala desde PowerShell:

```powershell
$hash = Get-Content .\TenchyShell-vX.Y.Z-win-x64.zip.sha256
.\Install-TenchyShell.ps1 -ArchivePath .\TenchyShell-vX.Y.Z-win-x64.zip -ExpectedSha256 $hash
```

El bootstrapper instala TenchyShell en `%LOCALAPPDATA%\TenchyShell\app`, detecta WezTerm y Yazi e instala los paquetes oficiales de WinGet cuando faltan. Usa `-SkipDependencies` si ya los gestionas manualmente y `-WhatIf` para revisar las operaciones sin escribir ni instalar. Requiere WinGet y red solo para las dependencias faltantes.

La instalación nunca instala un navegador: el perfil inicial usa `msedge.exe`, que puedes cambiar en el TOML.

### Desde el código fuente

Requiere el SDK de .NET 10:

```powershell
git clone https://github.com/Tenchys/tenchyshell.git
Set-Location tenchyshell
dotnet restore TenchyShell.slnx
dotnet build TenchyShell.slnx -c Release
.\scripts\publish.ps1 -Configuration Release
```

La publicación queda en:

```text
publish\TenchyShell\Release\win-x64\
```

Puedes copiar esa carpeta, por ejemplo, a:

```text
%LOCALAPPDATA%\TenchyShell\
```

### Ejecutar la instalación publicada

Desde la carpeta publicada:

```powershell
.\TenchyShell.exe TenchyShell.example.toml
```

La publicación de release es auto-contenida. Para desarrollo, el SDK de .NET 10 sigue siendo necesario.

No uses `--without-explorer` en la sesión principal. Ese modo debe probarse únicamente en una VM o usuario secundario.

## Comandos de desarrollo

Ejecutar desde la raíz del repositorio:

```powershell
# Restaurar dependencias
dotnet restore TenchyShell.slnx

# Compilar todos los proyectos
dotnet build TenchyShell.slnx

# Ejecutar las pruebas
dotnet test TenchyShell.slnx

# Ejecutar las pruebas aisladas del bootstrapper (requiere Pester 5 o posterior)
.\scripts\test-installer.ps1

# Ejecutar la aplicación mínima
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj

# Ejecutar usando un archivo TOML específico
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- config/TenchyShell.example.toml

# Diagnosticar configuración y dependencias sin iniciar el shell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --check config/TenchyShell.example.toml

# Ejecutar una sesión acotada que se cierra limpiamente (benchmarks/integración)
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --exit-after-seconds 60 config/TenchyShell.example.toml

# Probar un lanzamiento concreto sin iniciar el message loop
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --launch terminal
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --launch files
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --launch browser

# Acciones de sesión: requieren confirmación explícita y cierran/reinician la sesión real
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session logout --confirm
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session shutdown --confirm
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session restart --confirm

# Publicar el ejecutable para Windows x64
dotnet publish src/TenchyShell.App/TenchyShell.App.csproj -c Release -r win-x64 --self-contained false

# Publicar en publish/TenchyShell/Debug/win-x64 o publish/TenchyShell/Release/win-x64
.\scripts\publish.ps1 -Configuration Debug
.\scripts\publish.ps1 -Configuration Release
```

La compilación debe finalizar sin advertencias ni errores. Las pruebas no deben apagar, reiniciar ni cerrar sesión en la máquina.

`--check` comprueba que estén disponibles el terminal, el shell de comandos, Yazi y el navegador configurado. Las ausencias se reportan como advertencias y no impiden el arranque normal; el modo de comprobación termina con código `2` si falta alguna dependencia.

## Configuración TOML y ubicación predeterminada

Tras la instalación, la aplicación carga automáticamente la configuración del usuario si se inicia sin argumentos:

```text
%USERPROFILE%\.config\tenchyshell\config.toml
```

El instalador crea ese archivo solo si no existe; nunca sobrescribe una configuración existente. También deja los scripts de ejemplo bajo `.config\tenchyshell\scripts` sin reemplazar archivos que ya existan. Una ruta TOML pasada como argumento tiene prioridad. Si no hay archivo en la ruta predeterminada, la aplicación usa los valores incorporados.

En una publicación generada desde este repositorio, el archivo se copia automáticamente a:

```text
publish\TenchyShell\Release\win-x64\TenchyShell.example.toml
```

El archivo `TenchyShell.without-explorer.example.toml` es el perfil de prueba sin Explorer. TenchyShell acepta cualquier ruta TOML como primer argumento:

```powershell
.\TenchyShell.exe C:\ruta\TenchyShell.toml
```

Si no se proporciona una ruta, la aplicación usa los valores incorporados en el código; no modifica ni crea archivos TOML automáticamente. Los comentarios de los archivos de ejemplo describen cada opción, incluidas hotkeys, terminal, panel, zonas y tamaño proporcional de los números del overlay.

## Estructura

```text
src/TenchyShell.App          Ciclo de vida y composición de la aplicación
src/TenchyShell.Core         Dominio independiente de Windows
src/TenchyShell.Win32        Interoperabilidad nativa encapsulada
src/TenchyShell.Workspaces   Workspaces y gestión futura de ventanas
src/TenchyShell.UI           UI futura, inicialmente reservada
tests/                         Pruebas automatizadas
config/                        Configuraciones de ejemplo
logs/                          Marcador de la carpeta de logs de desarrollo
```

## Configuración y logs

La configuración normal está en [`config/TenchyShell.example.toml`](config/TenchyShell.example.toml) y evita atajos reservados por Windows. Para la prueba aislada sin Explorer usa [`config/TenchyShell.without-explorer.example.toml`](config/TenchyShell.without-explorer.example.toml). La aplicación puede recibir la ruta del archivo TOML como primer argumento; si no se proporciona, utiliza los valores por defecto.

El modo `--launch` inicia un proceso y termina inmediatamente; sirve también para diagnosticar terminal, Yazi y navegador sin registrar hotkeys.

### Migración desde MinimalShell

En el primer inicio, TenchyShell revisa `%LOCALAPPDATA%\MinimalShell` y copia
únicamente archivos conocidos hacia `%LOCALAPPDATA%\TenchyShell`: perfiles TOML,
el estado del fondo y el log heredado. La migración es idempotente, no borra el
origen y no sobrescribe un destino diferente; cualquier conflicto queda en
consola y en `%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log`.

Durante la transición, TenchyShell adquiere los mutex de ambos nombres para
impedir que una versión antigua y una nueva registren hotkeys simultáneamente.
Los perfiles antiguos también pueden seguir pasándose explícitamente por ruta.

## Rendimiento

El protocolo, las métricas y los criterios para aceptar optimizaciones están en
[`docs/PERFORMANCE.md`](docs/PERFORMANCE.md). Los datos crudos se guardan en
`%LOCALAPPDATA%\TenchyShell\benchmarks\v2\<batch-id>\` y no se versionan porque
contienen metadatos de la máquina y de sus procesos. Antes de una captura
oficial puede verificarse el instrumental con
`scripts\test-performance.ps1`; los smoke tests quedan marcados como no
oficiales y no pueden mezclarse con la comparativa final.

## Hotkeys

Los valores de `[hotkeys]` en el TOML se registran al iniciar el shell. Las combinaciones repetidas se rechazan antes de entrar en modo operativo; si Windows u otra aplicación ya reservó una combinación opcional, el error queda en consola y en el log, mientras TenchyShell continúa funcionando.

- `Ctrl+Alt+Enter`: terminal.
- `Ctrl+Alt+E`: terminal con Yazi.
- `Ctrl+Alt+Space`: launcher.
- `Ctrl+Alt+B`: navegador.
- `Ctrl+Alt+Shift+E`: recuperación, inicia `explorer.exe`.
- `Ctrl+Alt+1..9`: cambiar al workspace 1..9.
- `Ctrl+Alt+Shift+1..9`: mover la ventana activa al workspace 1..9.
- `Ctrl+Win+1..9`: colocar la ventana activa en la zona de layout configurada.

`Win+Q` solicita el cierre normal de la ventana activa. Las aplicaciones pueden mostrar sus propios diálogos de guardado; TenchyShell nunca termina el proceso de forma forzada.

Las teclas de función `F1` a `F24` pueden usarse sin modificadores. Si F12 no está reservado en tu sesión, puedes cambiar el launcher así:

```toml
[hotkeys]
launcher = "F12"
```

Detén cualquier instancia anterior de TenchyShell y vuelve a iniciarla con ese archivo TOML para aplicar el cambio.

Los hotkeys de workspaces se configuran en `[hotkeys.workspaces]` mediante `switch_1` a `switch_9` y `move_1` a `move_9`.

Las operaciones básicas de ventana usan `[hotkeys.window]`: flechas para mover, `resize_grow`/`resize_shrink` para cambiar tamaño, `maximize`, `restore` y `focus`. Las operaciones respetan el área de trabajo del monitor y no terminan procesos.

El layout inicial se define en `[layout]` y usa dos columnas con una fila (`1x2`). Las zonas se expresan con coordenadas normalizadas y las hotkeys se configuran en `[hotkeys.layout]`. El layout selecciona el monitor exacto, `primary` o `*`, y usa el área de trabajo real con DPI por monitor. Durante el arrastre, `Ctrl+Shift` muestra un overlay click-through y permite soltar la ventana en la zona resaltada.

## Funciones principales

- Lanzamiento del terminal configurado, Yazi y navegador.
- Launcher nativo para buscar aplicaciones instaladas y aplicaciones MSIX.
- Ejecución de comandos `!comando` en PowerShell interactivo dentro de WezTerm.
- Workspaces 1..9, cambio de workspace y traslado de ventanas.
- Movimiento, redimensionamiento, maximización, restauración y foco de ventanas.
- Layout por zonas con `Ctrl+Win+1..9`.
- Overlay de arrastre con `Ctrl+Shift` para seleccionar una zona visualmente.
- Layouts independientes por monitor, coordenadas negativas y DPI por monitor.
- Panel informativo auto-ocultable con workspace y hora local.
- Bandeja propia Win32 con `Ctrl+Alt+T`, independiente de Explorer.
- Cierre cooperativo de la ventana activa.
- Recuperación mediante `explorer.exe`.
- Acciones de sesión para cerrar sesión, apagar o reiniciar, siempre con `--confirm`.
- Diagnóstico de dependencias y logs en `%LOCALAPPDATA%\TenchyShell\logs\`.

La aplicación no reemplaza permanentemente Winlogon, no implementa una taskbar
completa y no incluye búsqueda web, plugins ni un administrador de archivos
interno. Su bandeja es propia y solo expone integraciones declaradas.

### Bandeja del sistema

`Ctrl+Alt+T` abre la superficie de bandeja propia de TenchyShell. También puede abrirse haciendo clic sobre el panel izquierdo cuando está visible; el menú aparece desplegado a su derecha. No inicia, enfoca ni usa `explorer.exe`. La superficie ya tiene navegación por teclado y ciclo de vida propio; los iconos de aplicaciones de terceros requieren integración explícita porque Windows no ofrece una API pública para enumerarlos o reubicarlos automáticamente.

Los elementos estáticos y dinámicos se configuran en `[system_tray]`. Un elemento dinámico ejecuta el `command` con sus `arguments` y debe devolver JSON con `text`, `tooltip`, `icon`, `state` y `action`. Consulta `scripts/mouse-battery.example.ps1` como ejemplo; el script puede reemplazarse por uno específico del fabricante del dispositivo.

### Idioma de teclado

El elemento `Idioma` de la bandeja muestra el método de entrada activo de la
ventana que tenía el foco antes de abrirla. Selecciónalo con `Enter` o clic para
ver los idiomas y distribuciones que Windows ya tiene habilitados; la opción
activa se marca con `*`. Al elegir otro, TenchyShell solicita el cambio a esa
ventana y actualiza el estado sin iniciar Explorer ni instalar paquetes de
idioma.

La sección opcional `[input_language]` permite deshabilitar el elemento,
cambiar su título, usar `label_format = "short"` (`ES`, `EN`) o `"full"`, y
definir un `hotkey` para abrir directamente el selector. Algunos IME,
aplicaciones elevadas, UWP/WinUI o sesiones remotas pueden rechazar o aplicar el
cambio de forma diferida; el error se registra y la configuración avanzada se
mantiene en Windows.

Para validar o publicar el MVP 0.7:

```powershell
dotnet test TenchyShell.slnx
.\scripts\publish.ps1 -Configuration Release
.\publish\TenchyShell\Release\win-x64\TenchyShell.exe --check .\publish\TenchyShell\Release\win-x64\TenchyShell.example.toml
```

## Panel informativo

El panel opcional se configura en `[status_panel]` y está habilitado por defecto:

```toml
[status_panel]
enabled = true
hotkey = "Ctrl+Alt+S"
width = 220
height = 96
edge_zone = 4
monitor = "primary"
```

Permanece oculto al iniciar. `Ctrl+Alt+S` lo muestra u oculta de forma persistente; llevar el mouse al borde izquierdo lo muestra temporalmente y se oculta al salir del panel. No recibe foco ni comandos y solo muestra el workspace activo y la hora local.

## Launcher mínimo

El launcher propio se abre con `Ctrl+Alt+Space` por defecto y busca aplicaciones del Menú Inicio y de `shell:AppsFolder`. La búsqueda funciona con el teclado: escribe texto, usa las flechas y presiona `Enter` para lanzar; `Escape` cancela. El hotkey se puede cambiar en `[hotkeys].launcher`; `Win+Space` y `Win+Alt+Space` pueden estar reservados por Windows o PowerToys.

Para ejecutar un comando en la terminal configurada, escribe `!` seguido del comando y presiona `Enter`:

```text
!ipconfig
!git status
!dotnet build
```

El comando se ejecuta en una sesión persistente de PowerShell dentro de WezTerm. TenchyShell usa `wezterm-gui.exe` para los lanzamientos de escritorio, evitando una consola auxiliar de `wezterm.exe`. La ventana del launcher pide confirmación antes de abrir la terminal.

Los logs de ejecución deben escribirse en:

```text
%LOCALAPPDATA%\TenchyShell\logs\
```

La carpeta `logs/` del repositorio solo documenta la ubicación reservada; no debe contener logs reales ni datos de usuario.

## Prueba del message loop y recuperación

Ejecutar el shell desde una consola de desarrollo:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj
```

La aplicación debe permanecer ejecutándose sin mostrar una ventana propia. Durante el desarrollo:

- `Ctrl+C` solicita un cierre limpio y libera la ventana de mensajes y el hotkey registrado.
- el hotkey configurado en `recovery` inicia `explorer.exe` como mecanismo de recuperación.

### Modo de prueba sin Explorer

Solo en una VM o usuario secundario, TenchyShell puede detener Explorer tras haber registrado el hotkey de recuperación:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --without-explorer config/TenchyShell.without-explorer.example.toml
```

El programa exige escribir `DETENER` en una consola interactiva antes de cerrar `explorer.exe`; después registra los hotkeys opcionales. No modifica Winlogon. Usa el hotkey configurado en `recovery` para iniciar Explorer nuevamente.

La prueba sin Explorer debe hacerse únicamente en una máquina virtual o con un usuario secundario. No modificar Winlogon. Para recuperar una sesión, pulsa el hotkey de recuperación, espera que Explorer aparezca y luego cierra TenchyShell con `Ctrl+C`.

## Seguridad durante el desarrollo

No cambiar permanentemente la configuración de Winlogon. Las pruebas sin `explorer.exe` deben realizarse primero en una máquina virtual o con un usuario secundario y siempre debe conservarse una ruta para iniciar `explorer.exe` manualmente.

La verificación manual completa y las limitaciones conocidas están en [`docs/MANUAL_TESTING.md`](docs/MANUAL_TESTING.md).
