# Registro de optimizaciones — MVP 0.7.6

Este registro separa mejoras demostrables de ideas pendientes. Una observación
estática puede justificar la eliminación de trabajo duplicado, pero no se usa
para afirmar que TenchyShell consume menos que Explorer; esa conclusión exige
las mediciones de `docs/PERFORMANCE.md`.

## OPT-001 — Consulta de red asíncrona y no duplicada

- Estado: aceptada.
- Hipótesis: abrir el dock bloquea el message loop y enumera WLAN dos veces
  cuando se usa la lista integrada.
- Evidencia previa: `CreateBuiltInItems` llamaba a `GetNetworkText()`, que
  ejecutaba `NetworkService.GetSnapshot()`, y `LoadItems` volvía a llamar al
  mismo servicio inmediatamente. Ambas llamadas ocurrían antes de mostrar la
  ventana.
- Cambio: el elemento integrado comienza como `Consultando estado...`; una sola
  tarea ejecuta `GetSnapshot` fuera del message loop, impide actualizaciones
  solapadas y publica el resultado de vuelta en el loop.
- Resultado verificable por conteo: consultas al abrir el dock, `2 -> 1`;
  consultas WLAN síncronas en el message loop, `2 -> 0`.
- Riesgos controlados: conserva el último snapshot, elimina filas obsoletas
  antes de agregar las nuevas, ignora resultados tras `Dispose` y mantiene los
  errores visibles.
- Regresión: build sin advertencias y 77 pruebas automatizadas correctas.
- Medición pendiente: latencia de primer frame del dock y P95 durante el flujo
  manual en una sesión controlada.

## Ideas no aceptadas todavía

- Mantener abierto permanentemente un handle de log.
- Cachear catálogos de ventanas, aplicaciones, red o fondos.
- Reducir timers o cambiar intervalos globalmente.
- Cambiar recolección de memoria o modo de publicación de .NET.

Estas ideas requieren una línea base válida y no deben implementarse solo por
intuición.

## Validación del instrumental

El recolector se ejecutó contra una sesión TenchyShell acotada con cierre limpio
y produjo cinco muestras válidas, incluyendo la shell y sus herramientas. El
resumen calculó CPU, memoria, I/O, handles e hilos sin valores agregados vacíos.
Esta ejecución es un smoke test de una repetición en un árbol de trabajo sucio,
por lo que no constituye la línea base ni se usa para comparar con Explorer.
Los datos permanecen en `%LOCALAPPDATA%\TenchyShell\benchmarks\smoke-valid\`.
