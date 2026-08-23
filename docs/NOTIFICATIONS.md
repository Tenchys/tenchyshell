# Notificaciones sin Explorer

TenchyShell puede mostrar notificaciones Toast de Windows sin depender de
`explorer.exe`. La recepción utiliza un bridge MSIX opcional con la capacidad
`userNotificationListener`; TenchyShell y el bridge se comunican solo por un
named pipe de la sesión del usuario. No hay polling.

## Requisitos y alcance

- Windows 11 Pro y TenchyShell 0.7.10 o posterior.
- El bridge se instala por usuario. Cada usuario que use TenchyShell debe
  instalar su propia copia del MSIX y conceder su propio permiso.
- TenchyShell no debe ejecutarse elevado. La autorización es una decisión de
  Windows para el usuario actual.
- La aplicación que origina el aviso decide si expone un logo. Cuando Windows
  lo proporciona, TenchyShell lo muestra; WezTerm, por ejemplo, actualmente no
  entrega uno mediante esta API.

Los avisos nuevos aparecen hasta seis segundos abajo a la derecha. Durante la
sesión, el historial está en `Ctrl+Alt+T` → `Notificaciones`. No se ejecutan
acciones ni contenido de las aplicaciones desde TenchyShell.

## Habilitación para desarrollo

1. En una PowerShell abierta en el repositorio, empaqueta e instala el bridge:

   ```powershell
   .\scripts\package-notification-bridge.ps1 -Install
   ```

   El script crea o reutiliza un certificado **solo de desarrollo** para el
   usuario actual, firma el MSIX y lo instala. Para una máquina compartida, un
   administrador puede usar `-TrustForAllUsers` una vez para confiar el
   certificado de desarrollo; aun así, cada usuario debe instalar el MSIX y
   aprobar el permiso en su perfil.

2. Activa las notificaciones en el TOML que se usará:

   ```toml
   [notifications]
   enabled = true
   ```

3. Inicia TenchyShell normalmente. Al detectar la opción, abre en segundo
   plano `TenchyShellNotificationBridge.exe`. En la primera ejecución Windows
   pide acceso a las notificaciones. Acéptalo. También se puede solicitar de
   forma explícita:

   ```powershell
   TenchyShellNotificationBridge.exe --request-access
   ```

4. Envía una notificación desde una aplicación real y comprueba que aparece la
   tarjeta y el historial de la bandeja.

Para deshabilitar completamente la función, usa `enabled = false` y reinicia
TenchyShell. No se inicia el bridge ni se conserva historial entre sesiones.

## Verificación y diagnóstico

La tarjeta local de benchmark comprueba solo la interfaz:

```powershell
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj --no-build -- --without-explorer --test-notification .\config\TenchyShell.without-explorer.example.toml
```

Requiere `[benchmark].enabled = true` además de las notificaciones. Para
probar el flujo completo con un emisor MSIX independiente:

```powershell
.\scripts\package-notification-test-sender.ps1 -Install
TenchyShellNotificationTestSender.exe
```

El emisor de prueba no queda ejecutándose y usa un icono visible para comprobar
el renderizado. Es una herramienta de desarrollo, no se distribuye con el
producto.

Los registros son:

- `%LOCALAPPDATA%\TenchyShell\logs\tenchyshell.log`: ciclo de vida y conexión.
- `%LOCALAPPDATA%\TenchyShell\logs\notification-bridge.log`: permiso,
  conexión y eventos recibidos por el bridge.
- `%LOCALAPPDATA%\TenchyShell\benchmarks\live\`: eventos
  `notification_received` cuando el benchmark está habilitado.

Si el bridge no se conecta, confirma que `[notifications].enabled = true`, que
el MSIX está instalado para el usuario actual con `Get-AppxPackage
TenchyShell.NotificationBridge`, y que el permiso no fue denegado. Reinstalar
el MSIX no requiere cerrar Explorer ni cambiar Winlogon.

## Distribución

Los certificados y paquetes creados por los scripts son solo para desarrollo.
Una distribución real debe firmar el mismo MSIX con un certificado de
publicación confiable y proporcionar el paquete a cada usuario. No copiar el
certificado de desarrollo ni ejecutar el bridge elevado como solución de
producción.
