# Checklist manual — MVP 0.1

Ejecutar estas pruebas en Windows 11 Pro. Antes de comenzar, confirmar que `wezterm-gui.exe`, `yazi.exe` y el navegador indicado en TOML están disponibles.

## Preparación

```powershell
dotnet build MinimalShell.slnx
dotnet test MinimalShell.slnx
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- config/MinimalShell.example.toml
```

El perfil normal usa `Ctrl+Alt+Enter`, `Ctrl+Alt+E`, `Ctrl+Alt+Space`, `Ctrl+Alt+B` y `Ctrl+Alt+Q` para no competir con atajos reservados de Windows.

## Perfil normal

- [ ] Confirmar que existe un log en `%LOCALAPPDATA%\MinimalShell\logs\minimalshell.log`.
- [ ] `Ctrl+Alt+Space`: el launcher aparece, recibe foco, busca una aplicación Win32 y una MSIX; `Enter` las abre y `Escape` cancela.
- [ ] `Ctrl+Alt+Enter`: abre WezTerm.
- [ ] `Ctrl+Alt+E`: abre Yazi en una única ventana de WezTerm.
- [ ] `Ctrl+Alt+B`: abre el navegador configurado.
- [ ] `Ctrl+Alt+Q`: cierra Bloc de notas y permite su diálogo normal de guardado cuando corresponde.
- [ ] `Ctrl+Alt+Shift+E`: inicia Explorer si no estaba ejecutándose.
- [ ] Cerrar MinimalShell con `Ctrl+C`; confirmar en el log que se liberaron hotkeys y recursos.
- [ ] Intentar abrir una segunda instancia; confirmar que informa que ya existe una en ejecución.

## Perfil sin Explorer

Solo en una VM o usuario secundario:

```powershell
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --without-explorer config/MinimalShell.without-explorer.example.toml
```

- [ ] Escribir `DETENER` y confirmar que Explorer se cierra tras registrar el hotkey de recuperación.
- [ ] Probar launcher, terminal, Yazi, navegador y cierre de ventana.
- [ ] Pulsar `Ctrl+Alt+Shift+E` y comprobar que Explorer vuelve a iniciarse.
- [ ] Cerrar MinimalShell con `Ctrl+C` y restaurar la sesión normal.

## Acciones destructivas

Solo en una sesión desechable. Las acciones requieren `--confirm` y no se deben ejecutar como parte de las pruebas automatizadas:

```powershell
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session logout --confirm
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session shutdown --confirm
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --session restart --confirm
```

## Limitaciones conocidas

- `Win+Enter` está reservado por Windows, incluso sin Explorer; el perfil alternativo usa `Ctrl+Alt+Enter`.
- Otros `Win+…` pueden ser reservados por Windows o por aplicaciones instaladas; MinimalShell informa cada conflicto en consola y log.
- MinimalShell no reemplaza permanentemente el shell de Winlogon, no ofrece taskbar, tray, workspaces ni gestión avanzada de ventanas.
- El catálogo de aplicaciones se limita al Menú Inicio y `shell:AppsFolder`; no busca ejecutables en todo el disco.
- La ejecución `!comando` abre PowerShell en WezTerm, pero todavía no muestra una pantalla de confirmación independiente dentro del launcher.
