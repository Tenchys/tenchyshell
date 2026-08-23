# Arquitectura

TenchyShell mantiene dependencias unidireccionales y concentra Win32 en una
capa dedicada. DWM sigue siendo el compositor; Explorer es solo recuperación.

```text
TenchyShell.App
 ├─ TenchyShell.Workspaces ── TenchyShell.Win32 ── TenchyShell.Core
 ├─ TenchyShell.Win32 ──────────────────────────── TenchyShell.Core
 └─ TenchyShell.Core

TenchyShell.NotificationBridge ─────────────────── TenchyShell.Core
TenchyShell.NotificationTestSender (herramienta MSIX de prueba)
```

| Componente | Descripción | Dependencias |
| --- | --- | --- |
| `TenchyShell.App` | Entrada, ciclo de vida, TOML y composición de servicios. | Core, Win32, Workspaces. |
| `TenchyShell.Core` | Modelos, configuración, comandos, logging y estado. | Tomlyn 0.18.0. |
| `TenchyShell.Win32` | P/Invoke: ventanas, hotkeys, monitores, foco, red y surfaces. | Core, Win32/DWM. |
| `TenchyShell.Workspaces` | Asociación de HWND, visibilidad y foco. | Core, Win32. |
| `TenchyShell.UI` | Ensamblado reservado para UI futura. | Plataforma Windows. |
| `NotificationBridge` | MSIX que reenvía `UserNotificationListener` por pipe local. | Core, WinRT, MSIX. |
| `NotificationTestSender` | Emisor MSIX independiente para pruebas de integración. | WinRT, MSIX. |
| `tests/*` | Pruebas unitarias de Core, Win32 y Workspaces. | xUnit. |

## Datos y fronteras

- Configuración TOML y migración idempotente, no destructiva, desde MinimalShell.
- Estado, logs e iconos temporales en `%LOCALAPPDATA%\TenchyShell`.
- Notificaciones: Windows → bridge MSIX → pipe local → centro de sesión, sin
  polling ni historial persistente.
- Las APIs nativas no se dispersan fuera de Win32; la lógica no gráfica vive en
  Core y debe poder probarse sin sesión gráfica.
