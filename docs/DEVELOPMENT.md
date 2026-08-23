# Desarrollo y mantenimiento

## Ciclo básico

```powershell
dotnet restore TenchyShell.slnx
dotnet build TenchyShell.slnx
dotnet test TenchyShell.slnx
dotnet run --project src/TenchyShell.App/TenchyShell.App.csproj -- --check .\config\TenchyShell.example.toml
```

Los proyectos usan .NET 10; la integración Windows usa `net10.0-windows`. La
dependencia NuGet de producto es `Tomlyn 0.18.0` en Core. Las pruebas usan
Microsoft.NET.Test.Sdk, xUnit y su runner.

## Scripts

| Script | Uso |
| --- | --- |
| `publish.ps1` | Publica artefactos `win-x64` auto-contenidos. |
| `Install-TenchyShell.ps1` | Instala un ZIP de release y valida checksum. |
| `test-installer.ps1` | Pruebas Pester del bootstrapper. |
| `invoke-performance-benchmark.ps1` | Ejecuta la medición reproducible. |
| `measure-performance.ps1`, `summarize-performance.ps1`, `test-performance.ps1` | Recolección, resumen y validación de rendimiento. |
| `package-notification-bridge.ps1` | Publica, firma e instala el bridge MSIX de desarrollo. |
| `package-notification-test-sender.ps1` | Empaqueta el emisor MSIX de integración. |
| `mouse-battery.example.ps1` | Ejemplo de elemento dinámico de bandeja. |

Los scripts de MSIX necesitan Windows SDK (`MakeAppx.exe` y `SignTool.exe`). El
bridge usa un certificado local de desarrollo; una release debe usar un
certificado de publicación confiable. Consulta [Notificaciones](NOTIFICATIONS.md).

## Publicación y convenciones

```powershell
.\scripts\publish.ps1 -Configuration Release
.\scripts\test-installer.ps1
```

Antes de publicar, ejecuta pruebas .NET, Pester y `--check`; valida ZIP,
checksum y bootstrapper en una VM o usuario secundario. No modifiques Winlogon.

- Mantén `TenchyShell` en ensamblados, namespaces y rutas; `MinimalShell` solo
  corresponde a migración, compatibilidad o historia.
- Encapsula P/Invoke en Win32 y conserva dependencias unidireccionales.
- Añade pruebas para lógica que no requiere sesión gráfica.
- No introduzcas polling, timers, cachés ni procesos persistentes sin hipótesis
  de coste y medición reproducible.
- Actualiza estas guías al cambiar configuración, scripts o arquitectura.
