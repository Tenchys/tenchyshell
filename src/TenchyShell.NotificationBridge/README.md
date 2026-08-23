# TenchyShell.NotificationBridge

Bridge MSIX opcional que recibe notificaciones de otras aplicaciones mediante
`UserNotificationListener` y las reenvía al pipe local de TenchyShell. No
presenta UI propia; se inicia solo cuando `[notifications].enabled = true`.

La instalación por usuario, permiso de Windows, empaquetado, diagnóstico y
distribución están documentados en [Notificaciones](../../docs/NOTIFICATIONS.md).
