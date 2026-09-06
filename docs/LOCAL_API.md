# API local de TenchyShell para YASB

La API está deshabilitada por defecto. Para activarla, añade `[api]` al TOML y
reinicia TenchyShell:

```toml
[api]
enabled = true
```

La API escucha únicamente en el Named Pipe `TenchyShell.LocalApi.v1`, con una
ACL limitada al SID del usuario actual. No abre HTTP, TCP ni otro puerto de
red. El límite es de cuatro conexiones simultáneas y 64 KiB por mensaje. Las
solicitudes deben completarse en cinco segundos; una suscripción puede quedar
abierta mientras el cliente siga conectado.

## Protocolo v1

El transporte usa NDJSON UTF-8: cada línea es un objeto JSON independiente.

```json
{"version":1,"id":"1","type":"request","method":"workspaces.get"}
{"version":1,"id":"2","type":"subscribe","topics":["workspace","windows"]}
{"version":1,"id":"3","type":"request","method":"workspace.switch","params":{"workspace":2}}
```

Las respuestas tienen `type = "response"`, `ok`, `id` y `result` o `error`.
Las suscripciones devuelven un snapshot inicial y después eventos con
`type = "event"`, `topic` y `data`.

Métodos disponibles: `workspace.active.get`, `workspaces.get`,
`applications.get`, `windows.get`, `workspace.switch` y
`window.active.move`. Los dos últimos aceptan `params.workspace` entre 1 y 9.

Los errores usan códigos estables como `invalid_request`,
`unsupported_version`, `unknown_method`, `invalid_workspace`,
`no_foreground_window`, `window_operation_rejected`, `message_too_large`,
`timeout` y `connection_limit`.

La consulta de aplicaciones es de sólo lectura y no expone rutas ni argumentos
de lanzamiento. Un `HWND` se devuelve como texto hexadecimal; puede quedar
inválido o ser reutilizado después de cerrar una ventana. Las ventanas elevadas,
WinUI/MSIX, `always-on-top` y otras ventanas especiales siguen las restricciones
de foco y UIPI impuestas por Windows.

La API no inicia ni cierra procesos, no ejecuta acciones administrativas y no
controla Explorer. La recuperación explícita mediante Explorer permanece
disponible aunque Explorer esté detenido temporalmente.

El cliente de referencia está en `examples/yasb/tenchyshell_client.py`.

## Uso desde la TUI

El menú principal de `TenchyShell.Tui` incluye `API local`. Esta pantalla es
únicamente de consulta: muestra el catálogo v1 y, si `[api].enabled = true`,
obtiene un snapshot al entrar y recibe actualizaciones push para `workspace`,
`windows` y `applications`. No ejecuta `workspace.switch` ni
`window.active.move`, no habilita la API, no inicia servicios ni inicia
`explorer.exe`.

Con la API deshabilitada se muestra el catálogo y una guía para editar el TOML.
Si el pipe no existe, deja de responder o TenchyShell se cierra, la TUI muestra
`API no disponible` o un error y continúa abierta. `R` permite reintentar al
abrir la conexión; al volver al menú se cierra la suscripción. La recuperación
mediante el hotkey configurado sigue siendo independiente de esta pantalla.

Ejemplo de estado conectado:

```text
Estado: conectada
Protocolo: v1
Pipe: TenchyShell.LocalApi.v1
Workspace activo: 2
Workspaces: 9
Aplicaciones: 42
Ventanas: 4
```
