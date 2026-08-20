# TenchyShell.Core

Lógica independiente de Windows: configuración, comandos, procesos y modelos de dominio.

Las interfaces de este proyecto deben poder probarse sin una sesión gráfica activa. Las implementaciones que llamen a Win32 deben permanecer en `TenchyShell.Win32`.
