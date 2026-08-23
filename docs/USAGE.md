# Uso y configuración

Inicia TenchyShell con un TOML. Para uso diario, copia y edita
`config/TenchyShell.example.toml`.

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- .\config\TenchyShell.example.toml
```

`--check <toml>` valida dependencias y configuración sin registrar hotkeys ni
cambiar la sesión. Reinicia el shell para aplicar cambios TOML.

## Uso diario

- **Launcher:** busca aplicaciones Win32 y MSIX; `Enter` abre y `Escape` cierra.
- **Workspaces:** cada uno conserva sus ventanas; las demás se ocultan al
  cambiar. El hotkey configurado mueve la activa entre workspaces.
- **Selector:** `Alt+Tab` muestra las ventanas del workspace actual.
- **Layout:** las zonas aplican posición y tamaño a la ventana activa; el
  modificador configurado muestra el overlay de arrastre.
- **Dock y bandeja:** ofrecen terminal, Yazi, navegador, red, idioma, elementos
  configurados e historial de notificaciones sin depender de Explorer.
- **Recuperación:** el hotkey `recovery` inicia Explorer solo por petición.

Ventanas elevadas, protegidas, UWP/WinUI o juegos pueden no aceptar foco u
operaciones como una ventana Win32 convencional.

## Referencia TOML

| Sección | Campos principales | Efecto |
| --- | --- | --- |
| `[terminal]` | comandos y argumentos | Terminal y comando que abre Yazi. |
| `[file_manager]` | `command` | Ejecutable del gestor de archivos. |
| `[launcher]`, `[applications]` | `enabled`, `command`, `browser` | Launcher y navegador. |
| `[status_panel]`, `[system_tray]` | `enabled`, `hotkey`, tamaño | Dock y bandeja propios. |
| `[window_switcher]` | `enabled`, `hotkey`, tamaño | Selector del workspace actual. |
| `[input_language]` | etiqueta, título, hotkey | Selector de idiomas habilitados por Windows. |
| `[wallpaper]` | carpeta, extensiones, monitor | Fondo propio detrás de las ventanas. |
| `[layout]` y `[[layout.zones]]` | preset, zonas, monitor | Layout de ventanas y overlay. |
| `[hotkeys]` y subtablas | combinaciones globales | Acciones del shell, workspaces, ventanas y layout. |
| `[benchmark]` | perfil, intervalo, retención, cuota | Registro de sesión real. |
| `[notifications]` | `enabled` | Bridge MSIX opcional por usuario. |

Los hotkeys son globales y pueden fallar si Windows u otra aplicación los
reserva; TenchyShell lo registra sin terminar la sesión.

### Elementos de bandeja

`[[system_tray.items]]` define un elemento. Sin `command` es estático; con
`command` ejecuta un proceso y recibe JSON con `text`, `tooltip`, `icon`,
`state` y `action`. Las acciones se declaran con
`[system_tray.actions."<id>".<acción>]`. El ejemplo
`scripts/mouse-battery.example.ps1` muestra el formato y debe tener timeout
acotado.

### Benchmark y notificaciones

Benchmark está desactivado por defecto y puede registrar títulos de ventanas y
comandos en `%LOCALAPPDATA%\TenchyShell\benchmarks\live`; revísalos antes de
compartir. Consulta [Rendimiento](PERFORMANCE.md).

`[notifications].enabled = true` requiere bridge MSIX y permiso por usuario;
consulta [Notificaciones](NOTIFICATIONS.md).
