# Checklist manual — MVP 0.5

Ejecutar estas pruebas en Windows 11 Pro. Antes de comenzar, confirmar que `wezterm-gui.exe`, `yazi.exe` y el navegador indicado en TOML están disponibles.

## Preparación

```powershell
dotnet build MinimalShell.slnx
dotnet test MinimalShell.slnx
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- config/MinimalShell.example.toml
```

El perfil normal usa `Ctrl+Alt+Enter`, `Ctrl+Alt+E`, `Ctrl+Alt+Space`, `Ctrl+Alt+B` y `Ctrl+Alt+Q` para no competir con atajos reservados de Windows.

## Diagnóstico y release — Hito 0.5

Antes de iniciar una sesión de prueba, ejecutar:

```powershell
dotnet run --project src/MinimalShell.App/MinimalShell.App.csproj -- --check config/MinimalShell.example.toml
```

El comando no registra hotkeys, no inicia ventanas y no detiene Explorer. Debe mostrar `[OK]` para WezTerm, PowerShell, Yazi y el navegador configurado. Si una dependencia no está instalada, corregirla o confirmar que la advertencia es esperada.

Para generar artefactos reproducibles:

```powershell
.\scripts\publish.ps1 -Configuration Debug
.\scripts\publish.ps1 -Configuration Release
```

Verificar que existan `publish/Debug/win-x64/MinimalShell.dll`, `publish/Release/win-x64/MinimalShell.dll` y ambos archivos TOML de ejemplo. Probar cada publicación con `--check` antes de iniciar el perfil normal.

## Perfil normal

- [ ] Confirmar que existe un log en `%LOCALAPPDATA%\MinimalShell\logs\minimalshell.log`.
- [ ] `Ctrl+Alt+Space`: el launcher aparece, recibe foco, busca una aplicación Win32 y una MSIX; `Enter` las abre y `Escape` cancela.
- [ ] `Ctrl+Alt+Enter`: abre WezTerm.
- [ ] `Ctrl+Alt+E`: abre Yazi en una única ventana de WezTerm.
- [ ] `Ctrl+Alt+B`: abre el navegador configurado.
- [ ] `Ctrl+Alt+Q`: cierra Bloc de notas y permite su diálogo normal de guardado cuando corresponde.
- [ ] `Ctrl+Alt+1` y `Ctrl+Alt+2`: alternan entre dos workspaces sin terminar las aplicaciones.
- [ ] `Ctrl+Alt+Shift+2`: mueve la ventana activa al workspace 2; al cambiar allí, vuelve a mostrarse y recibe foco.
- [ ] `Ctrl+Alt+Left/Right/Up/Down`: mueve la ventana activa dentro del área del monitor.
- [ ] `Ctrl+Alt+Shift+Right/Left`: aumenta y reduce el tamaño sin salir del monitor.
- [ ] `Ctrl+Alt+M` y `Ctrl+Alt+R`: maximiza y restaura la ventana activa.
- [ ] `Ctrl+Alt+F`: devuelve el foco a la ventana activa válida.
- [ ] `Ctrl+Alt+S`: muestra y oculta el panel informativo sin robar el foco; el borde izquierdo lo muestra temporalmente.
- [ ] El panel refleja el workspace activo y actualiza la hora cada segundo aproximadamente.
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
- MinimalShell no reemplaza permanentemente el shell de Winlogon y no ofrece taskbar, tray ni gestión avanzada de ventanas.
- El catálogo de aplicaciones se limita al Menú Inicio y `shell:AppsFolder`; no busca ejecutables en todo el disco.
- La ejecución `!comando` abre PowerShell en WezTerm después de confirmar dentro del launcher.
## Hito 0.4 — Panel informativo

Con MinimalShell ejecutándose y Explorer disponible:

1. Confirma que no aparece ninguna ventana del panel al iniciar.
2. Pulsa `Ctrl+Alt+S`; debe aparecer un panel de aproximadamente `220x96` en el centro del borde izquierdo.
3. Verifica que la aplicación activa conserva el foco.
4. Pulsa nuevamente `Ctrl+Alt+S`; el panel debe ocultarse.
5. Lleva el cursor a los primeros 4 píxeles del borde izquierdo; el panel debe aparecer.
6. Aleja el cursor del panel; debe ocultarse automáticamente.
7. Vuelve a mostrarlo con el hotkey, mueve el cursor y confirma que permanece visible.
8. Cambia de workspace con los hotkeys configurados y confirma que `Workspace N` se actualiza.
9. Cierra MinimalShell con `Ctrl+C` y confirma que el panel desaparece sin errores.

Repite la comprobación en una VM o usuario secundario con el perfil `config/MinimalShell.without-explorer.example.toml`.
