# MinimalShell

Shell minimalista para Windows 11 Pro, orientado al teclado y diseñado para reducir la dependencia cotidiana de `explorer.exe` sin reemplazar el compositor DWM ni la compatibilidad con aplicaciones Win32.

## Estado actual

El MVP 0.1 candidato incluye configuración TOML, logging, message loop Win32, recuperación de Explorer, launcher nativo, hotkeys configurables y cierre cooperativo de la ventana activa. La aplicación mantiene una única instancia por sesión.

## Requisitos

- Windows 11 Pro.
- .NET SDK 10.0 o posterior compatible con `net10.0`.
- PowerShell.
- WezTerm y Yazi para las pruebas de integración.

## Comandos de desarrollo

Ejecutar desde la raíz del repositorio:

```powershell
# Restaurar dependencias
dotnet restore MinimalShell.slnx

# Compilar todos los proyectos
dotnet build MinimalShell.slnx

# Ejecutar las pruebas
dotnet test MinimalShell.slnx

# Ejecutar la aplicación mínima
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj

# Ejecutar usando un archivo TOML específico
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- config/MinimalShell.example.toml

# Probar un lanzamiento concreto sin iniciar el message loop
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --launch terminal
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --launch files
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --launch browser

# Acciones de sesión: requieren confirmación explícita y cierran/reinician la sesión real
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session logout --confirm
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session shutdown --confirm
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session restart --confirm

# Publicar el ejecutable para Windows x64
dotnet publish src/MinimalShell.App/MinimalShell.App.csproj -c Release -r win-x64 --self-contained false
```

La compilación debe finalizar sin advertencias ni errores. Las pruebas no deben apagar, reiniciar ni cerrar sesión en la máquina.

## Estructura

```text
src/MinimalShell.App          Ciclo de vida y composición de la aplicación
src/MinimalShell.Core         Dominio independiente de Windows
src/MinimalShell.Win32        Interoperabilidad nativa encapsulada
src/MinimalShell.Workspaces   Workspaces y gestión futura de ventanas
src/MinimalShell.UI           UI futura, inicialmente reservada
tests/                         Pruebas automatizadas
config/                        Configuraciones de ejemplo
logs/                          Marcador de la carpeta de logs de desarrollo
```

## Configuración y logs

La configuración normal está en [`config/MinimalShell.example.toml`](config/MinimalShell.example.toml) y evita atajos reservados por Windows. Para la prueba aislada sin Explorer usa [`config/MinimalShell.without-explorer.example.toml`](config/MinimalShell.without-explorer.example.toml). La aplicación puede recibir la ruta del archivo TOML como primer argumento; si no se proporciona, utiliza los valores por defecto.

El modo `--launch` inicia un proceso y termina inmediatamente; sirve también para diagnosticar terminal, Yazi y navegador sin registrar hotkeys.

## Hotkeys

Los valores de `[hotkeys]` en el TOML se registran al iniciar el shell. Las combinaciones repetidas se rechazan antes de entrar en modo operativo; si Windows u otra aplicación ya reservó una combinación opcional, el error queda en consola y en el log, mientras MinimalShell continúa funcionando.

- `Ctrl+Alt+Enter`: terminal.
- `Ctrl+Alt+E`: terminal con Yazi.
- `Ctrl+Alt+Space`: launcher.
- `Ctrl+Alt+B`: navegador.
- `Ctrl+Alt+Shift+E`: recuperación, inicia `explorer.exe`.

`Win+Q` solicita el cierre normal de la ventana activa. Las aplicaciones pueden mostrar sus propios diálogos de guardado; MinimalShell nunca termina el proceso de forma forzada.

Las teclas de función `F1` a `F24` pueden usarse sin modificadores. Si F12 no está reservado en tu sesión, puedes cambiar el launcher así:

```toml
[hotkeys]
launcher = "F12"
```

Detén cualquier instancia anterior de MinimalShell y vuelve a iniciarla con ese archivo TOML para aplicar el cambio.

## Launcher mínimo

El launcher propio se abre con `Ctrl+Alt+Space` por defecto y busca aplicaciones del Menú Inicio y de `shell:AppsFolder`. La búsqueda funciona con el teclado: escribe texto, usa las flechas y presiona `Enter` para lanzar; `Escape` cancela. El hotkey se puede cambiar en `[hotkeys].launcher`; `Win+Space` y `Win+Alt+Space` pueden estar reservados por Windows o PowerToys.

Para ejecutar un comando en la terminal configurada, escribe `!` seguido del comando y presiona `Enter`:

```text
!ipconfig
!git status
!dotnet build
```

El comando se ejecuta en una sesión persistente de PowerShell dentro de WezTerm. MinimalShell usa `wezterm-gui.exe` para los lanzamientos de escritorio, evitando una consola auxiliar de `wezterm.exe`. La ventana del launcher pide confirmación antes de abrir la terminal.

Los logs de ejecución deben escribirse en:

```text
%LOCALAPPDATA%\MinimalShell\logs\
```

La carpeta `logs/` del repositorio solo documenta la ubicación reservada; no debe contener logs reales ni datos de usuario.

## Prueba del message loop y recuperación

Ejecutar el shell desde una consola de desarrollo:

```powershell
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj
```

La aplicación debe permanecer ejecutándose sin mostrar una ventana propia. Durante el desarrollo:

- `Ctrl+C` solicita un cierre limpio y libera la ventana de mensajes y el hotkey registrado.
- el hotkey configurado en `recovery` inicia `explorer.exe` como mecanismo de recuperación.

### Modo de prueba sin Explorer

Solo en una VM o usuario secundario, MinimalShell puede detener Explorer tras haber registrado el hotkey de recuperación:

```powershell
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --without-explorer config/MinimalShell.without-explorer.example.toml
```

El programa exige escribir `DETENER` en una consola interactiva antes de cerrar `explorer.exe`; después registra los hotkeys opcionales. No modifica Winlogon. Usa el hotkey configurado en `recovery` para iniciar Explorer nuevamente.

La prueba sin Explorer debe hacerse únicamente en una máquina virtual o con un usuario secundario. No modificar Winlogon. Para recuperar una sesión, pulsa el hotkey de recuperación, espera que Explorer aparezca y luego cierra MinimalShell con `Ctrl+C`.

## Seguridad durante el desarrollo

No cambiar permanentemente la configuración de Winlogon. Las pruebas sin `explorer.exe` deben realizarse primero en una máquina virtual o con un usuario secundario y siempre debe conservarse una ruta para iniciar `explorer.exe` manualmente.

Consulta [`AGENTS.md`](AGENTS.md) para las reglas completas del proyecto y [`plan.md`](plan.md) para la secuencia de hitos.

La verificación manual completa y las limitaciones conocidas están en [`docs/MANUAL_TESTING.md`](docs/MANUAL_TESTING.md).
