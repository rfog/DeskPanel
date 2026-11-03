# DeskPanel

Panel flotante para Windows (atajo global, por defecto `Ctrl+Alt+Espacio`) que replica parte del flujo de DEVONthink:
feeds de Inoreader a PDF con vista previa y anotación, búsqueda en índices propios y acceso rápido a carpetas.

- Requisitos: SDK .NET 10, WebView2 Runtime.
- Compilar: `dotnet build DeskPanel.sln`
- Configuración de ejemplo: `config-samples/`
- La versión para macOS está en `src/DeskPanel.Mac` (compilar: `dotnet build DeskPanel.Mac.slnf`). Véase [MACOS_PORT.md](MACOS_PORT.md).

## Licencia

Copyright © 2026 RFOG.

DeskPanel es software libre: puedes redistribuirlo y modificarlo bajo los términos de la Licencia Pública General de GNU
publicada por la Free Software Foundation, ya sea la versión 3 o, a tu elección, cualquier versión posterior
(GPL-3.0-or-later). Se distribuye sin ninguna garantía. Texto completo en [LICENSE](LICENSE).

Los componentes de terceros conservan sus licencias: véase [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
