# Uso y configuración

Inicia TenchyShell con un TOML. Para uso diario, copia y edita
`config/TenchyShell.example.toml`.

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- .\config\TenchyShell.example.toml
```

`--check <toml>` valida dependencias y configuración sin registrar hotkeys ni
cambiar la sesión. Reinicia el shell para aplicar cambios TOML.

Al iniciar, TenchyShell abre una pestaña del terminal configurado con su TUI.
La TUI también puede ejecutarse directamente con `TenchyShell.exe --tui
<ruta.toml>`; no crea ventanas Win32 ni registra hotkeys. `--without-tui`
mantiene el shell sin abrir esa pestaña durante diagnóstico o recuperación.

## Diagnóstico comparativo de activación MSIX/UWP

`--diagnose-packaged` ejecuta la activación oficial por `AppUserModelId` dentro
de una instancia real de TenchyShell. Compara `CLSCTX_INPROC_SERVER` y
`CLSCTX_LOCAL_SERVER` sin usar `explorer.exe` como fallback ni abrir la TUI.
Se puede repetir un AUMID; `both` es el contexto predeterminado y el cierre
acotado es obligatorio.

```powershell
TenchyShell.exe --diagnose-packaged <AUMID-1> --diagnose-packaged <AUMID-2> `
  --activation-context both --exit-after-seconds 30 --without-tui config.toml
```

La consola muestra AUMID, contexto, estado de Explorer, PID, HRESULT y las
marcas temporales de cada intento. Los logs permanentes solo registran el
contexto, el estado de Explorer, el resultado y el HRESULT. Obtén los AUMID
reales con `Get-StartApps` antes de probar. Para la variante sin Explorer usa
solo una VM o usuario secundario y `--automated-benchmark --without-explorer`;
el mecanismo de benchmark restaura Explorer al finalizar.

### Comparación ETW controlada

Para atribuir una espera en `ActivateApplication`, el script
[`trace-packaged-activation-dependency.ps1`](../scripts/trace-packaged-activation-dependency.ps1)
captura dos ETL comparables: primero sin Explorer y después con Explorer
iniciado explícitamente. Requiere Windows Performance Toolkit (`wpr.exe`) y una
VM o usuario secundario que ya comience sin `Shell_TrayWnd` ni `explorer.exe`.
No usa Explorer como fallback del launcher, no toca Winlogon, el registro,
paquetes ni servicios.

Obtén los AUMID de Configuración y Calculadora con `Get-StartApps` y ejecuta,
por ejemplo, contra un binario ya compilado:

```powershell
.\scripts\trace-packaged-activation-dependency.ps1 `
  -TenchyShellPath .\src\TenchyShell.App\bin\Debug\net10.0-windows\TenchyShell.exe `
  -ConfigurationPath .\config\TenchyShell.without-explorer.example.toml `
  -AppUserModelId '<AUMID-Configuracion>', '<AUMID-Calculadora>' `
  -AcknowledgeIsolatedSession
```

La confirmación de PowerShell es deliberada. El script crea el perfil `.wprp`
en `%TEMP%`, lo elimina en `finally` y guarda los ETL, salida de consola y un
resumen JSON en `%LOCALAPPDATA%\TenchyShell\diagnostics`. Cada traza queda en
memoria y no puede durar más de 60 segundos; TenchyShell termina justo tras la
matriz de diagnóstico cuando no hay bloqueo. Si llega a iniciar Explorer para
el escenario B, el bloque `finally` solicita su salida cooperativa mediante
TenchyShell y valida que `Shell_TrayWnd` desapareció.

En WPA, correlaciona el hilo de TenchyShell bloqueado en
`ActivateApplication` con COM/RPC, CLSID y el PID/imagen del servidor. Declara
una dependencia directa de Explorer solo si el destino es `explorer.exe` (o un
servidor COM alojado explícitamente por él) y el escenario B elimina la espera.
Si aparece `RuntimeBroker.exe`, `dllhost.exe`, `svchost.exe` u otro proceso, la
dependencia es de ese broker o componente de sesión. Si B funciona pero la
traza no identifica el servidor, documenta únicamente una dependencia del
entorno iniciado por Explorer. Los ETL incluyen rutas, PID y actividad de la
sesión: consérvalos localmente solo hasta documentar la conclusión y elimínalos
después de revisarlos.

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
| `[OPTIONALS]` | `dock`, `application_search` | Superficies opcionales; ambas activas por defecto. |
| `[launcher]`, `[applications]` | `enabled`, `command`, `browser` | Launcher y navegador. |
| `[status_panel]`, `[system_tray]` | `enabled`, `hotkey`, tamaño | Dock y bandeja propios. |
| `[window_switcher]` | `enabled`, `hotkey`, tamaño | Selector del workspace actual. |

### Dock por categorías y Yazi

El dock conserva por defecto las categorías `equipo`, `aplicaciones` y `sesion`.
Cada una admite `id`, `title`, `order`, `visible` y una lista `items` con
`id`, `title`, `action` y `visible`. Si `[dock]` o `categories` no existen se
usan los valores integrados; `recovery` se añade siempre a la sesión. Sólo se
aceptan acciones integradas y el orden debe ser positivo y único.

Las acciones `logout`, `shutdown` y `restart` requieren dos pulsaciones de la
misma acción. Escape, cerrar el dock o elegir otra acción cancela la solicitud;
una acción en ejecución no admite activaciones duplicadas.

La vista `Atajos de Yazi` lee `%YAZI_CONFIG_HOME%\keymap.toml` cuando la variable
existe, o `%APPDATA%\yazi\config\keymap.toml` como alternativa. Si el archivo no
existe, muestra el preset predeterminado integrado en TenchyShell, identificado
como `preset integrado`; no crea ningún archivo en AppData. Muestra capas,
secuencias, modificadores, acciones y descripciones sin ejecutar `run`, plugins
o comandos. Ausente, inválido, ilegible y TOML corrupto se informan como estados
separados.
| `[input_language]` | etiqueta, título, hotkey | Selector de idiomas habilitados por Windows. |
| `[wallpaper]` | carpeta, extensiones, monitor | Fondo propio detrás de las ventanas. |
| `[layout]` y `[[layout.zones]]` | preset, zonas, monitor | Layout de ventanas y overlay. |
| `[hotkeys]` y subtablas | combinaciones globales | Acciones del shell, workspaces, ventanas y layout. |
| `[benchmark]` | perfil, intervalo, retención, cuota | Registro de sesión real. |
| `[notifications]` | `enabled` | Bridge MSIX opcional por usuario. |
| `[tui]`, `[[tui.widgets]]` | habilitación, título, tipo y fuente | Interfaz de consola y widgets aislados. |

Los hotkeys son globales y pueden fallar si Windows u otra aplicación los
reserva; TenchyShell lo registra sin terminar la sesión.

`[OPTIONALS].dock = false` deshabilita el panel y su hotkey, mientras que
`[OPTIONALS].application_search = false` deshabilita el catálogo, la ventana y
el hotkey del launcher. `[status_panel].enabled = false` y
`[launcher].enabled = false` también deshabilitan respectivamente esas
superficies. La sección es opcional, sus dos valores predeterminados son
`true`, y los cambios se aplican al siguiente inicio. Un valor no booleano se
rechaza antes del message loop.

### Elementos de bandeja

`[[system_tray.items]]` define un elemento. Sin `command` es estático; con
`command` ejecuta un proceso y recibe JSON con `text`, `tooltip`, `icon`,
`state` y `action`. Las acciones se declaran con
`[system_tray.actions."<id>".<acción>]`. El ejemplo
`scripts/mouse-battery.example.ps1` muestra el formato y debe tener timeout
acotado.

### Benchmark y notificaciones

Benchmark está desactivado por defecto y puede registrar títulos de ventanas y
comandos en `%LOCALAPPDATA%\TenchyShell\benchmarks\live`; no registra una
decisión por cada ventana enumerada y limita los agregados de enumeración al
intervalo de muestra. Revísalos antes de compartir. Consulta
[Rendimiento](PERFORMANCE.md).

Para revisar una sesión sin cargar los JSONL completos, ejecuta:

```powershell
.\scripts\summarize-live-telemetry.ps1
```

El script selecciona el último `sessionId`, calcula deltas entre la primera y
última muestra y resume la actividad y los errores de telemetría y logs. Acepta
`-SessionId`, `-LiveDirectory` y `-LogDirectory` para diagnóstico dirigido; no
imprime títulos o comandos de eventos normales. `-IncludeErrorMessages` añade
el texto de las entradas de error de log.

La opción `Telemetría` de la TUI ejecuta este mismo resumen al abrirse y con
`R`, sin actualizarse en segundo plano de forma periódica. La salida se puede
desplazar con flechas, PageUp/PageDown, Home y End. La TUI busca el script junto
al TOML efectivo, en el árbol de desarrollo o junto a la instalación; si no lo
encuentra muestra el error y conserva la TUI operativa.

`[notifications].enabled = true` requiere bridge MSIX y permiso por usuario;
consulta [Notificaciones](NOTIFICATIONS.md).

### TUI y widgets

`[tui] enabled` controla el inicio automático y `title` define su encabezado.
Un widget puede ser `static` (texto en `source`), `configuration` (por ejemplo
`hotkeys.terminal` o `terminal.command`) o `command`. Los comandos externos
usan exclusivamente `command` y `arguments` declarados en TOML y se ejecutan
fuera del proceso del shell, con `timeout_ms` (100–60000),
`max_output_bytes` (64–1048576) y actualización manual (`update_ms = 0`). Un
fallo o timeout se muestra en ese widget y se registra sin cerrar la sesión.
