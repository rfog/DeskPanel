# Avisos de terceros

DeskPanel se distribuye bajo la licencia GPL-3.0-or-later (véase [LICENSE](LICENSE)). Incluye o usa los componentes
siguientes, cada uno bajo su propia licencia.

## Incluidos en este repositorio

Todo está en `assets/pdfjs/` (distribución de PDF.js 6.3.289) y conserva sus ficheros de licencia.

| Componente | Licencia | Fichero de licencia |
| --- | --- | --- |
| PDF.js (Mozilla) | Apache-2.0 | `assets/pdfjs/LICENSE` |
| CMaps de Adobe | BSD-3-Clause | `assets/pdfjs/web/cmaps/LICENSE` |
| Perfil ICC CGATS001 | CC0-1.0 | `assets/pdfjs/web/iccs/LICENSE` |
| Fuentes Foxit (PDFium) | BSD-3-Clause | `assets/pdfjs/web/standard_fonts/LICENSE_FOXIT` |
| Fuentes Liberation (Red Hat) | GPL-2.0 con excepciones para fuentes | `assets/pdfjs/web/standard_fonts/LICENSE_LIBERATION` |
| JBIG2 (PDFium) | BSD-3-Clause | `assets/pdfjs/web/wasm/LICENSE_JBIG2` |
| OpenJPEG | BSD-2-Clause | `assets/pdfjs/web/wasm/LICENSE_OPENJPEG` |
| qcms | MIT | `assets/pdfjs/web/wasm/LICENSE_QCMS` |
| QuickJS (compilado a wasm) | MIT | Sin fichero propio en la distribución de PDF.js |
| Adaptadores wasm de PDF.js | Apache-2.0 | `assets/pdfjs/web/wasm/LICENSE_PDFJS_*` |

Las fuentes Liberation son ficheros independientes que el visor carga como datos: se distribuyen junto a DeskPanel, no
forman parte de él y siguen bajo su propia licencia. Su código fuente está en
<https://github.com/liberationfonts/liberation-fonts>.

## Paquetes NuGet

No están en el repositorio: se descargan al compilar y sus binarios acompañan a los paquetes publicados. Lista generada
de la restauración de los proyectos; las versiones y licencias corresponden a sus paquetes.

| Paquete | Versión | Licencia |
| --- | --- | --- |
| AngleSharp.Css | 1.0.2 | MIT |
| AngleSharp | 1.7.2 | MIT |
| AvalonEdit | 6.3.1.120 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD-3-Clause |
| Avalonia.BuildServices | 11.3.2 | MIT |
| Avalonia.Controls.WebView | 12.1.1 | MIT |
| Avalonia.Desktop | 12.1.4 | MIT |
| Avalonia.FreeDesktop.AtSpi | 12.1.4 | MIT |
| Avalonia.FreeDesktop | 12.1.4 | MIT |
| Avalonia.HarfBuzz | 12.1.4 | MIT |
| Avalonia.Native | 12.1.4 | MIT |
| Avalonia.Remote.Protocol | 12.1.4 | MIT |
| Avalonia.Skia | 12.1.4 | MIT |
| Avalonia.Themes.Fluent | 12.1.4 | MIT |
| Avalonia.Win32 | 12.1.4 | MIT |
| Avalonia.X11 | 12.1.4 | MIT |
| Avalonia | 12.1.4 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT |
| HarfBuzzSharp | 8.3.1.3 | MIT |
| HtmlSanitizer | 9.2.1039 | MIT |
| J2N | 2.1.0 | Apache-2.0 |
| Lucene.Net.Analysis.Common | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net.Highlighter | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net.Memory | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net.Queries | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net.QueryParser | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net.Sandbox | 4.8.0-beta00017 | Apache-2.0 |
| Lucene.Net | 4.8.0-beta00017 | Apache-2.0 |
| MicroCom.Runtime | 0.11.6 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 8.0.0 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.0 | MIT |
| Microsoft.Extensions.DependencyInjection | 8.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | MIT |
| Microsoft.Extensions.Logging | 8.0.0 | MIT |
| Microsoft.Extensions.Options | 8.0.0 | MIT |
| Microsoft.Extensions.Primitives | 8.0.0 | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT |
| Microsoft.Web.WebView2 | 1.0.4258.31 | BSD-3-Clause |
| PdfPig | 0.1.16 | Apache-2.0 |
| PuppeteerSharp | 25.13.1 | MIT |
| ReactiveExtensionsSharp | 0.3.0 | MIT |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT |
| SkiaSharp | 3.119.4 | MIT |
| SmartReader | 0.11.1 | Apache-2.0 |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| WebDriverBiDi | 0.0.61 | MIT |
| Whisper.net.Runtime.Metal | 1.9.1 | MIT |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT |
| Whisper.net.Runtime | 1.9.1 | MIT |
| Whisper.net | 1.9.1 | MIT |

## Herramientas externas (no se distribuyen)

Las descargan los scripts de `tools/` o la propia aplicación en el equipo del usuario, cada una bajo su licencia: FFmpeg,
DeepFilterNet, los modelos de Whisper (whisper.cpp) y Chromium (vía PuppeteerSharp). El runtime de WebView2 es un componente
de Microsoft instalado en el sistema.

## Marcas

DEVONthink, Inoreader y otros nombres citados son marcas de sus propietarios. DeskPanel es un proyecto independiente, sin
relación con ellos.
