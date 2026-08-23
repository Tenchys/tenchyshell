# Checklist manual — TenchyShell MVP 0.7

Ejecutar estas pruebas en Windows 11 Pro. Antes de comenzar, confirmar que `wezterm-gui.exe`, `yazi.exe` y el navegador indicado en TOML están disponibles.

## Preparación

```powershell
dotnet build TenchyShell.slnx
dotnet test TenchyShell.slnx
.\scripts\test-installer.ps1
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- config/TenchyShell.example.toml
```

El perfil normal usa `Ctrl+Alt+Enter`, `Ctrl+Alt+E`, `Ctrl+Alt+Space`, `Ctrl+Alt+B` y `Ctrl+Alt+Q` para no competir con atajos reservados de Windows.

## Diagnóstico y release — Hito 0.5

Antes de iniciar una sesión de prueba, ejecutar:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --check config/TenchyShell.example.toml
```

El comando no registra hotkeys, no inicia ventanas y no detiene Explorer. Debe mostrar `[OK]` para WezTerm, PowerShell, Yazi y el navegador configurado. Si una dependencia no está instalada, corregirla o confirmar que la advertencia es esperada.

Para generar artefactos reproducibles:

```powershell
.\scripts\publish.ps1 -Configuration Debug
.\scripts\publish.ps1 -Configuration Release
```

Verificar que existan `publish/TenchyShell/Debug/win-x64/TenchyShell.dll`, `publish/TenchyShell/Release/win-x64/TenchyShell.dll` y ambos archivos TOML de ejemplo. Probar cada publicación con `--check` antes de iniciar el perfil normal.

## Hito 0.7.7 — Release instalable

- [ ] Con el árbol limpio, ejecutar `dotnet test TenchyShell.slnx`, `./scripts/test-installer.ps1` y `./scripts/publish.ps1 -Configuration Release`.
- [ ] Crear el tag temporal `v0.7.7-test.1` y confirmar en GitHub Actions build Release, pruebas .NET, Pester, ZIP, checksum y GitHub Release.
- [ ] Descargar ZIP, `.sha256` y `Install-TenchyShell.ps1`; comprobar el checksum e instalar en una carpeta de prueba con el bootstrapper.
- [ ] Ejecutar `TenchyShell.exe --check` desde la instalación. Confirmar que WezTerm y Yazi se detectan o se instalan mediante WinGet.
- [ ] Cambiar el navegador en `%USERPROFILE%\.config\tenchyshell\config.toml`, reinstalar y confirmar que el TOML y scripts existentes se conservan.
- [ ] En un usuario secundario o VM, ejecutar el perfil publicado con `--without-explorer`, recuperar Explorer con `Ctrl+Alt+Shift+E` y confirmar que Winlogon no cambia.
- [ ] Conservar URL de CI, checksum, salida de `--check` y resultados de la sesión aislada antes de eliminar el release y tag temporales.

## Consolidación y rendimiento — Hito 0.7.6

- [ ] Con una copia de prueba en `%LOCALAPPDATA%\MinimalShell`, iniciar
  TenchyShell y confirmar que los perfiles conocidos, el estado válido del
  fondo y el log se copian a `%LOCALAPPDATA%\TenchyShell` sin borrar el origen.
- [ ] Repetir el inicio: no se duplican archivos ni se sobrescribe un destino
  diferente; los conflictos aparecen en consola y `tenchyshell.log`.
- [ ] Confirmar que no puede ejecutarse simultáneamente una instancia antigua
  que posea `Local\MinimalShell.SingleInstance` y una instancia TenchyShell.
- [ ] Abrir la bandeja y confirmar que aparece inmediatamente con `Red` en
  estado de consulta; la enumeración WLAN se completa después sin congelar el
  teclado, el mouse ni el message loop.
- [ ] Ejecutar las cuatro mediciones descritas en `docs/PERFORMANCE.md`, con
  cinco repeticiones por escenario y fase, y generar `summary.md` fuera del
  repositorio.
- [ ] Anotar hardware, build de Windows, commit, estado del árbol, procesos
  incluidos, muestras descartadas y limitaciones antes de comparar medianas.

## Perfil normal

- [ ] Confirmar que existe un log en `%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log`.
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
- [ ] `Ctrl+Alt+T`: muestra la bandeja propia sin iniciar ni enfocar Explorer; `Tab`/flechas navegan, `Enter` abre terminal/Yazi/navegador y `Escape` la cierra.
- [ ] Con al menos dos idiomas o distribuciones habilitados en Windows, abrir la bandeja sobre Notepad, seleccionar `Idioma` y elegir la otra opción con flechas y `Enter`; comprobar que el texto introducido usa la nueva distribución. El elemento activo se marca con `*` y el dock muestra su etiqueta (`ES`/`EN` por defecto).
- [ ] Repetir el cambio desde WezTerm y una aplicación MSIX. Si un IME o una aplicación elevada rechaza el cambio, confirmar que TenchyShell registra el error y continúa funcionando.
- [ ] Con `config/TenchyShell.example.toml`, el elemento `Mouse` actualiza su texto mediante `scripts/mouse-battery.example.ps1`; si no hay batería WMI, muestra `N/D` sin terminar el shell.
- [ ] `Ctrl+Alt+S`: muestra y oculta el panel informativo sin robar el foco; el borde izquierdo lo muestra temporalmente.
- [ ] Al hacer clic sobre el panel izquierdo visible, se abre el menú desplegable de bandeja a su derecha.
- [ ] En la bandeja, `Red` muestra el estado de las interfaces y `Enter` actualiza el centro propio sin iniciar Explorer.
- [ ] Si existe Wi-Fi, `Red` lista cada SSID con porcentaje de señal, protección y `[Conectar]`/`[Desconectar]`; seleccionar la acción actualiza el estado sin iniciar Explorer.
- [ ] Si existe Ethernet activa, `Red` indica `Cable`, la IP IPv4 y la velocidad negociada (`100 Mb/s`, `1 Gb/s` o similar), y permite `[Desconectar]`.
- [ ] Con permisos suficientes, una interfaz Ethernet inactiva ofrece `[Conectar]`; sin permisos, el error queda registrado sin bloquear TenchyShell.
- [ ] `Fondos` carga en segundo plano las imágenes soportadas de `Imágenes`; seleccionar una aplica el fondo sin iniciar Explorer.
- [ ] Tras seleccionar un fondo, cerrar y volver a iniciar TenchyShell: el mismo fondo se restaura desde `%LOCALAPPDATA%\TenchyShell\state\wallpaper.json`. Si la imagen fue eliminada, TenchyShell inicia sin bloquearse.
- [ ] El panel refleja el workspace activo y actualiza la hora cada segundo aproximadamente.
- [ ] `Ctrl+Alt+Shift+E`: inicia Explorer si no estaba ejecutándose.
- [ ] Cerrar TenchyShell con `Ctrl+C`; confirmar en el log que se liberaron hotkeys y recursos.
- [ ] Intentar abrir una segunda instancia; confirmar que informa que ya existe una en ejecución.

## Perfil sin Explorer

Solo en una VM o usuario secundario:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --without-explorer config/TenchyShell.without-explorer.example.toml
```

- [ ] Escribir `DETENER` y confirmar que Explorer se cierra tras registrar el hotkey de recuperación.
- [ ] Confirmar que el modo no cambia Winlogon ni mata automáticamente una nueva instancia de Explorer iniciada por Windows; la recuperación sigue siendo manual mediante el hotkey.
- [ ] Probar launcher, terminal, Yazi, navegador y cierre de ventana.
- [ ] Pulsar `Ctrl+Alt+Shift+E` y comprobar que Explorer vuelve a iniciarse.
- [ ] Pulsar `Ctrl+Alt+T`; confirmar que no inicia Explorer, muestra la bandeja propia y se cierra con `Escape`.
- [ ] Con dos distribuciones ya habilitadas, abrir `Idioma` desde la bandeja, cambiar la selección y escribir en Notepad o WezTerm; comprobar que funciona sin Explorer y que `Ctrl+Alt+Shift+E` sigue recuperándolo.
- [ ] Cerrar TenchyShell con `Ctrl+C` y restaurar la sesión normal.

## Acciones destructivas

Solo en una sesión desechable. Las acciones requieren `--confirm` y no se deben ejecutar como parte de las pruebas automatizadas:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session logout --confirm
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session shutdown --confirm
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --session restart --confirm
```

## Limitaciones conocidas

- `Win+Enter` está reservado por Windows, incluso sin Explorer; el perfil alternativo usa `Ctrl+Alt+Enter`.
- Otros `Win+…` pueden ser reservados por Windows o por aplicaciones instaladas; TenchyShell informa cada conflicto en consola y log.
- TenchyShell no reemplaza permanentemente el shell de Winlogon ni ofrece una taskbar completa; la bandeja propia no captura iconos privados de Explorer.
- El catálogo de aplicaciones se limita al Menú Inicio y `shell:AppsFolder`; no busca ejecutables en todo el disco.
- La ejecución `!comando` abre PowerShell en WezTerm después de confirmar dentro del launcher.
## Hito 0.4 — Panel informativo

Con TenchyShell ejecutándose y Explorer disponible:

1. Confirma que no aparece ninguna ventana del panel al iniciar.
2. Pulsa `Ctrl+Alt+S`; debe aparecer un panel de aproximadamente `220x96` en el centro del borde izquierdo.
3. Verifica que la aplicación activa conserva el foco.
4. Pulsa nuevamente `Ctrl+Alt+S`; el panel debe ocultarse.
5. Lleva el cursor a los primeros 4 píxeles del borde izquierdo; el panel debe aparecer.
6. Aleja el cursor del panel; debe ocultarse automáticamente.
7. Vuelve a mostrarlo con el hotkey, mueve el cursor y confirma que permanece visible.
8. Cambia de workspace con los hotkeys configurados y confirma que `Workspace N` se actualiza.
9. Cierra TenchyShell con `Ctrl+C` y confirma que el panel desaparece sin errores.

Repite la comprobación en una VM o usuario secundario con el perfil `config/TenchyShell.without-explorer.example.toml`.

## Hito 0.6.2 — Colocación por hotkeys

Con TenchyShell ejecutándose y una configuración con `[layout]` habilitado:

1. Abre Notepad y WezTerm.
2. Pulsa `Ctrl+Win+1`; la ventana activa debe ocupar la mitad izquierda.
3. Pulsa `Ctrl+Win+2`; la ventana activa debe ocupar la mitad derecha.
4. Maximiza una ventana y repite el paso anterior; debe restaurarse y colocarse.
5. Confirma que TenchyShell no roba el foco ni modifica su propio panel o launcher.
6. Pulsa `Ctrl+Win+3..9`; si no existen zonas configuradas, debe registrarse un mensaje claro sin terminar TenchyShell.

## Hito 0.6.3 — Overlay durante el arrastre

Con TenchyShell ejecutándose y `[layout].enabled = true`:

1. Abre Notepad o WezTerm y realiza un arrastre normal; el overlay no debe aparecer.
2. Mantén `Ctrl+Shift`, pulsa el botón izquierdo sobre la ventana y arrástrala; debe aparecer el overlay sin robar el foco.
3. Mueve el cursor entre las zonas; solo la zona bajo el cursor debe resaltarse.
4. Suelta sobre una zona; la ventana debe restaurarse si estaba maximizada y ocupar esa zona.
5. Repite el arrastre y pulsa `Escape`; la ventana debe conservar su geometría original.
6. Repite el arrastre y suelta `Ctrl` o `Shift`; el overlay debe cancelarse sin mover la ventana.
7. Repite el arrastre y suelta fuera de una zona; no debe cambiar la geometría.
8. Cierra TenchyShell con `Ctrl+C`; confirma en el log que el overlay desaparece y que los hooks se liberan sin terminar la sesión.

## Hito 0.6.4 — Multi-monitor y DPI

Con dos monitores conectados y `[layout]` habilitado:

1. Ejecuta `--check` y confirma que la configuración es válida.
2. Define un bloque de zonas con `monitor = "primary"` y otro con el identificador exacto de un monitor, por ejemplo `monitor = '\\.\DISPLAY2'`.
3. Abre Notepad en cada monitor y aplica `Ctrl+Win+1..9`; cada monitor debe usar su layout correspondiente.
4. Coloca un monitor a la izquierda del primario y confirma que las ventanas conservan coordenadas negativas válidas.
5. Repite la prueba arrastrando con `Ctrl+Shift` hacia una zona del monitor secundario.
6. Confirma que la ventana queda dentro del área de trabajo y no debajo de la taskbar.
7. Si los monitores tienen escalas DPI distintas, confirma que las zonas mantienen sus proporciones y que el overlay aparece en el monitor correcto.
8. Repite con `config/TenchyShell.without-explorer.example.toml` en una VM o usuario secundario.

## Hito 0.6.5 — Integración y release

1. Ejecuta `dotnet build TenchyShell.slnx` y confirma cero advertencias y errores.
2. Ejecuta `dotnet test TenchyShell.slnx` y confirma que Core y Workspaces pasan.
3. Ejecuta `--check` con ambos TOML de ejemplo; debe mostrar también el diagnóstico `[OK]` de layout.
4. Genera las publicaciones Debug y Release:

   ```powershell
   .\scripts\publish.ps1 -Configuration Debug
   .\scripts\publish.ps1 -Configuration Release
   ```

5. Ejecuta `--check` usando los TOML copiados dentro de `publish/TenchyShell/Debug/win-x64` y `publish/TenchyShell/Release/win-x64`.
6. Inicia la publicación Release, prueba hotkeys, launcher, panel y layout, y ciérrala con `Ctrl+C`.
7. Confirma en `%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log` que se liberaron hotkeys, hooks y recursos Win32.
8. Repite el cierre con Explorer activo y en una VM o usuario secundario sin Explorer.
