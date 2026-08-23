# Paquete MSIX del bridge

Este manifiesto entrega la identidad de paquete y la capacidad
`userNotificationListener` al ejecutable publicado desde
`src/TenchyShell.NotificationBridge`. Empaquetar requiere `MakeAppx.exe` y un
certificado de firma cuyo publicador coincida con `Identity/Publisher`.

`scripts/package-notification-bridge.ps1 -Install` publica, empaqueta, firma e
instala una variante de desarrollo por usuario. Genera un certificado local con
el mismo publicador del manifiesto y lo agrega a `CurrentUser\TrustedPeople` y
`CurrentUser\Root`, necesario para que Windows acepte el paquete autoemitido.
La variante de release debe sustituirlo por el certificado de publicación y no
debe confiar certificados autoemitidos.

El paquete declara el alias `TenchyShellNotificationBridge.exe`. TenchyShell lo
activa directamente cuando `[notifications] enabled = true`, sin iniciar
Explorer. Cada usuario debe instalar el MSIX y conceder su propio permiso de
notificaciones; con un certificado de publicación no hará falta instalar un
certificado de desarrollo en cada equipo.

Para una prueba con más de un usuario, ejecutar el script elevado con
`-TrustForAllUsers` agrega el certificado de desarrollo solo a
`LocalMachine\TrustedPeople`. Cada usuario instala después el mismo MSIX sin
elevar TenchyShell. No usar esta opción en una distribución de release.
