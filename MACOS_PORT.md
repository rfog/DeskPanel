# DeskPanel para macOS: especificación del port

Fecha: 2026-10-14

## Contexto

DeskPanel es una herramienta personal de un único usuario. La versión Windows está en este repositorio: WPF sobre .NET 10 (migrada desde .NET 8 en Windows antes de empezar este port), con los proyectos `DeskPanel.Core`, `DeskPanel.Feeds`, `DeskPanel.Search` y `DeskPanel.App`.

Esta tarea crea una versión macOS con Avalonia que reutiliza las librerías compartidas. En Mac, DEVONthink 4 se encarga de ver, anotar y organizar los PDFs. DeskPanel solo los genera y los deposita en una carpeta.

Antes de nada, lee el código de la versión Windows. Para la versión Mac, este documento prevalece si hay conflicto.

## Alcance

| Funcionalidad | Windows | macOS |
| --- | --- | --- |
| Lista de feeds de Inoreader, resumir, traducir | Sí | Sí |
| Generar PDF, en idioma original o traducido | WebView2 | Chrome headless (PuppeteerSharp) |
| Visor y anotación de PDF (pdf.js) | Sí | No: lo hace DEVONthink |
| Procesado de audio | Scripts Python (original) | Sí, sin Python |
| Búsqueda en índices propios (Lucene) | Sí | Sí, con índices nuevos creados en el Mac |
| Búsqueda en DEVONthink | No | Sí |
| Gestión de índices | Sí | Sí, solo carpetas que no estén en DEVONthink |
| Atajo global y panel flotante | Sí | No: ventana normal de macOS con botones |
| Web remota | Sí | No |
| Archivar PDFs | Sí | No |
| Verificar ficheros | Sí | No |
| Catálogo de carpetas | Sí | Pendiente de confirmar |

## Restricciones

- **Un solo usuario.** No hay sincronización de estado entre equipos ni concurrencia que resolver.
- **No se toca el proyecto WPF** (`DeskPanel.App`).
- **Windows no se puede verificar desde el Mac.** Los cambios en librerías compartidas deben seguir compilando allí, así que anota cada uno en `docs/CAMBIOS-PARA-WINDOWS.md` para comprobarlo en Windows.
- **Sin Python** en ninguna parte.
- **Comentarios del código en inglés**, profesionales, explicando el porqué y no el qué. Nada de comentarios obvios.
- **Escrituras atómicas** en todo fichero que vaya a una carpeta sincronizada o vigilada.

## Plataforma y estructura

- **App Mac:** Avalonia, última versión estable (verificar al crear el proyecto), con target `net10.0`.
- **Librerías existentes:** ya están en `net10.0`. Si al empezar alguna sigue en `net8.0`, para y avisa: la migración se hace y se verifica en Windows, no desde el Mac.
- **Proyectos nuevos:**
  - `DeskPanel.Mac`: la app Avalonia, con sus vistas y los servicios propios de macOS (búsqueda en DEVONthink, abrir ficheros y URLs con `open`).
  - `DeskPanel.Pdf.Chromium`: implementa `IPdfRenderer` con PuppeteerSharp. Es multiplataforma, así que Windows podría adoptarlo más adelante.
  - `DeskPanel.Audio`: el pipeline de audio sin Python, también multiplataforma.
- **`DeskPanel.Mac.slnf`:** filtro de solución que excluye `DeskPanel.App`.
- **Interfaces en Core** para lo que dependa de la plataforma; como mínimo `IPdfRenderer` y un servicio para abrir ficheros, carpetas y URLs.

## Fase 0: inventario, antes de escribir código

Informa al usuario de lo siguiente y espera su confirmación antes de refactorizar las librerías compartidas:

1. **Estado real de la versión Windows:**
   - cómo están implementados el resumen y la traducción (servicio, API, dónde están las claves);
   - formato y ubicación de la configuración;
   - nomenclatura de los PDFs y destinos de salida.
2. **Dependencias de Windows en Core, Feeds y Search:**
   - WebView2, `System.Windows.*`, WinForms, Win32 o COM, IFilter;
   - rutas con `\` o letras de unidad;
   - carpetas especiales con semántica de Windows.
3. **Separador de las rutas relativas** guardadas en los índices Lucene.
4. **Acoplamiento de la generación de PDF:** si está atada a WebView2 dentro de Feeds, propone extraerla a `IPdfRenderer`.

## Configuración y rutas

La configuración vive en una carpeta en la nube compartida con Windows, así que contiene rutas de Windows.

Hace falta resolver las rutas por máquina, por ejemplo con una configuración compartida más un fichero local por máquina con sus raíces:

- carpeta compartida;
- salida de PDFs;
- índices;
- modelos de audio.

Los perfiles CSS se comparten sin cambios. Propón la solución concreta en la Fase 0, según cómo esté hecha ahora.

## Ventana

Ventana normal de macOS, sin atajo global, sin panel flotante y sin icono en la bandeja obligatorio.

Botones:
- Feeds
- Audio
- Buscar en índices
- Buscar en DEVONthink
- Índices
- Carpetas (pendiente de confirmar)

## Feeds

- **Lista:** igual que en Windows, con entradas de Inoreader y estado de leído local.
- **Acciones por entrada:** Resumir, Traducir y PDF.
- **PDF:** se genera en el idioma original o en el traducido y se deposita en su destino. No se muestra.
- **Destinos:** los mismos que en Windows (carpeta de salida y copia en la carpeta compartida). Confirmar en la Fase 0.
- **Importación en DEVONthink:** en la carpeta compartida, una regla de DEVONthink importa los ficheros nuevos a su base de datos y los mueve a la Global Inbox. DeskPanel no habla con DEVONthink para esto.
- **Escritura atómica:** genera el PDF en un temporal fuera de la carpeta vigilada, en el mismo volumen, y muévelo con un rename. Así la regla nunca ve un PDF a medio escribir.
  - La carpeta compartida está en un proveedor de nube (Google Drive o Synology Drive). Comprueba que el rename funciona ahí.

## Generación de PDF

- **Motor:** PuppeteerSharp usando el Chrome instalado. La ruta del ejecutable es configurable; por defecto `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`.
- **Mismo HTML y perfiles CSS que en Windows.** Usa `PreferCSSPageSize = true` para que mande `@page`.
- **JavaScript desactivado** en la página, igual que en Windows.
- **Una sola instancia del navegador** reutilizada mientras la app está abierta, cerrada al salir.
- **Comprobar paridad:** genera el mismo artículo en Windows y en Mac y compara la paginación.

## Búsqueda

### Índices propios

- **Motor:** Lucene.Net, el mismo que en Windows.
- **Índices nuevos** creados en el Mac; no se copian los de Windows.
- **Ámbito:** solo carpetas que no están en DEVONthink. Se gestionan desde el botón Índices.
- **Ubicación:** almacenamiento local, fuera de carpetas sincronizadas, con un único escritor por índice.
- **Extractores de texto:** deben ser multiplataforma.

### DEVONthink

- **Botón propio.**
- **Canal:** Apple Events con JXA, ejecutado con `osascript -l JavaScript` y salida en JSON.
- **No uses el servidor MCP de DEVONthink.** Es una interfaz para agentes de IA, solo existe en las ediciones Pro y Server, y puede redactar el contenido.
- **Resultados:** nombre, base de datos, ubicación y tipo. Se abren con `open "x-devonthink-item://<uuid>"`.
- **Consulta:** se pasa tal cual, con la sintaxis de DEVONthink.
- **Límite de resultados** (por ejemplo, 200): cada lectura de propiedad es un viaje de ida y vuelta de Apple Events.
- **Permiso de Automatización (TCC):**
  - `NSAppleEventsUsageDescription` en el Info.plist;
  - app empaquetada como `.app` y firmada ad hoc (`codesign --force --deep -s -`).
  - Con `dotnet run`, el permiso se atribuye a Terminal o al IDE.
- **Si DEVONthink no está abierto,** Apple Events lo lanza. Hay que decidir si eso es aceptable o si se avisa antes.

Boceto orientativo. Verifica cada término contra el diccionario de DEVONthink 4 (Script Editor → Archivo → Abrir diccionario):

```js
function run(argv) {
  const dt = Application("DEVONthink");
  const limit = 200;
  // Each property read below is a separate Apple Event round trip,
  // so the hit list is capped before touching any property.
  const hits = dt.search(argv[0]).slice(0, limit);
  return JSON.stringify(hits.map(r => ({
    uuid: r.uuid(),
    name: r.name(),
    location: r.location(),
    database: r.database().name(),
    type: r.recordType()
  })));
}
```

## Audio, sin Python

Pipeline: entrada de audio → limpieza de ruido y normalización → transcripción → resumen → salida. La subida a Spreaker sigue siendo manual.

- **Limpieza:** ffmpeg (Homebrew) por línea de comandos, con `afftdn` o `arnndn` (RNNoise, que necesita un fichero de modelo) y después `loudnorm`. Si la calidad no llega a la de los scripts originales, evaluar DeepFilterNet, que tiene binario independiente.
- **Transcripción:** whisper.cpp a través de Whisper.net, con aceleración Metal o Core ML en Apple Silicon (verificar qué paquete de runtime hace falta). Los modelos ggml se guardan en una ruta configurable.
- **Resumen:** el mismo servicio que usan los feeds.
- **Referencia:** los scripts Python originales están (o estarán) en `docs/legacy-audio/`. Hay que replicar su comportamiento (parámetros, modelos, formato de salida), no su código.

## Fuera de alcance en Mac

- Visor y anotación de PDF
- Archivar PDFs
- Verificar ficheros
- Web remota
- Atajo global y panel flotante
- Búsqueda con Spotlight
- Copiar índices desde Windows
- Sincronización entre equipos

## Orden de trabajo

Cada fase compila y se ejecuta. El usuario confirma antes de pasar a la siguiente.

Requisito previo, hecho en Windows: solución completa migrada a .NET 10, compilando y funcionando.

0. Inventario.
1. Esqueleto Avalonia, `.slnf` y configuración por máquina.
2. Feeds: lista, resumir y traducir.
3. Generación de PDF y depósito atómico.
4. Índices propios y su gestión.
5. Búsqueda en DEVONthink, más el empaquetado `.app` que necesita el permiso de Automatización.
6. Audio.

## Preguntas abiertas

- ¿El catálogo de carpetas va en el Mac, sí o no?
- ¿Cuáles son los destinos exactos de los PDFs y cómo se nombra la versión traducida?
- Audio: ¿de dónde viene la entrada, a dónde va la salida y en qué idiomas se transcribe?
- ¿Cuál es la ruta de la carpeta compartida en el Mac, y es de Google Drive o de Synology Drive?

## Por verificar

- Versión estable actual de Avalonia y su compatibilidad con .NET 10.
- Que `global.json`, si existe, fije un SDK 10.0.x instalado en el Mac.
- Paquetes de runtime de Whisper.net para Apple Silicon.
- Términos JXA de DEVONthink 4: en particular, si `search` sin el parámetro `in` busca en todas las bases abiertas, y el nombre de la propiedad del tipo de registro.
- Que el rename sea atómico dentro de la carpeta del proveedor de nube.
- Cómo empaquetar el `.app`: con una herramienta tipo Dotnet.Bundle o con un script propio.
