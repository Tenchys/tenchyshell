# Operación segura

## Sesión normal y recuperación

Ejecuta TenchyShell primero con Explorer disponible. El hotkey `recovery`
inicia `explorer.exe` explícitamente si necesitas recuperar su superficie;
TenchyShell no lo usa como dependencia normal.

Eventos y excepciones quedan en `%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log`.
El bridge de notificaciones añade `notification-bridge.log`.

## Prueba sin Explorer

`--without-explorer` es solo para una VM o usuario secundario. Registra los
hotkeys de recuperación y solicita el cierre cooperativo de Explorer; no
modifica Winlogon ni debe ejecutarse en la sesión principal.

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --without-explorer .\config\TenchyShell.without-explorer.example.toml
```

Si falla una prueba, usa el hotkey de recuperación o ejecuta `explorer.exe`
desde una terminal. Conserva una terminal funcional antes de detener Explorer.

## Límites conocidos

- Ventanas elevadas, protegidas, UWP/WinUI, juegos o always-on-top pueden no
  enumerarse, moverse, ocultarse o recibir foco como una Win32 normal.
- La bandeja privada de Explorer no se captura; solo hay elementos propios y
  datos de APIs públicas.
- Notificaciones requieren permiso por usuario; el icono solo se dibuja si la
  aplicación origen lo expone.
- Redes, IME y adaptadores dependen de permisos y políticas de Windows; el
  error debe quedar observable sin terminar el shell.

## Datos locales

| Ruta | Contenido |
| --- | --- |
| `%LOCALAPPDATA%\TenchyShell\logs` | Logs de shell y bridge. |
| `%LOCALAPPDATA%\TenchyShell\state` | Fondo, migración y caché temporal de iconos. |
| `%LOCALAPPDATA%\TenchyShell\benchmarks\live` | Telemetría de benchmark, con posible información sensible. |

Revisa estos datos antes de compartirlos y retíralos deliberadamente si ya no
los necesitas.
