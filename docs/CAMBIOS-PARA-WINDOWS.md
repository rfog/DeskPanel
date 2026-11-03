# Cambios hechos desde el Mac que hay que comprobar en Windows

Todo lo de aquí se hizo en un Mac **sin poder ejecutar Windows**. Lo único verificado desde el Mac es que compila:

```
dotnet build src/DeskPanel.App/DeskPanel.App.csproj -p:EnableWindowsTargeting=true   # 0 errores, 0 avisos
```

Marca cada casilla cuando esté comprobado en Windows. Orden recomendado: 1 → 2 → 3 → 4 (de lo que puede romper el arranque a lo que solo cambia un detalle).

## 1. Al abrir el repositorio

- [ ] **`.gitattributes`** (`* text=auto eol=lf`, nuevo). Los ficheros de texto pasan a LF. Al abrir el repo en Windows git puede mostrar ficheros como
  modificados hasta ejecutar `git add --renormalize .`. Conviene hacerlo una vez y comitear el resultado.
- [ ] **`.gitignore`**: añadidos `.DS_Store` y `.idea/`.
- [ ] **`DeskPanel.sln`** incluye ahora `DeskPanel.Mac` (Avalonia) y `DeskPanel.Pdf.Chromium` (PuppeteerSharp). Comprobar que Visual Studio abre la solución y que
  `Build Solution` sigue limpio. Si molestan, se pueden quitar de la solución: `DeskPanel.Mac.slnf` es el filtro del Mac y `DeskPanel.App` no depende de ellos.
- [ ] **Una instalación limpia** (sin `machine.json` ni `deskpanel.json`) genera los mismos ficheros que antes: `ConfigService` bifurca por `OperatingSystem.IsMacOS()`
  y la rama de Windows es el código anterior (`CreateWindowsAppConfig`, sin cambios; `LocalAppDirectory` sigue siendo `%LOCALAPPDATA%\DeskPanel`).

## 2. Código compartido que Windows también ejecuta

### Extracción de servicios de IA de `DeskPanel.App` a `DeskPanel.Feeds`

- [ ] `FeedSummarizer`, `FeedTranslator`, `LlmSetup` y los registros `FeedSummaryOutcome`, `FeedTranslation`, `FeedTranslationOutcome` viven ahora en `DeskPanel.Feeds`
  (misma lógica; reciben `AppConfig`, `PathResolver`, carpeta de caché e `ISecretStore` en lugar de `Shell`).
- [ ] `DeskPanel.App` conserva los mismos nombres como **envoltorios finos** (`Feeds/FeedSummarizer.cs`, `Feeds/FeedTranslator.cs`, `Infrastructure/LlmSetup.cs`) que llaman a los compartidos
  con el `SecretStoreAdapter` (DPAPI). `Web/WebActions.cs` califica las dos llamadas con `global::DeskPanel.App.Feeds.` para evitar la ambigüedad de nombres.
  **Probar**: resumir y traducir una entrada desde la ventana de Feeds y desde la web, guardar un resumen, y resumir un audio (usa `LlmSetup`).
- [ ] `Core/Configuration/ISecretStore.cs` (nuevo: interfaz y `SecretNames`). `SecretStore.cs` añade `SecretStoreAdapter`; `SettingsViewModel.Ai.cs` toma las constantes de `SecretNames`
  (mismos textos, así que el `secrets.json` existente sigue valiendo). **Probar**: que las claves de IA y la contraseña de la web se siguen leyendo y guardando.
- [ ] `DeskPanel.Feeds.csproj` referencia ahora `DeskPanel.Audio` (por `ILlmClient`). Las dos librerías ya viajaban juntas en la app.

### Configuración (`AppConfig`)

- [ ] Propiedades nuevas, todas opcionales y **solo usadas por el Mac**: `Feeds.CssFolder`, `Feeds.ChromePath`, `Devonthink` (`DevonthinkConfig`, `DevonthinkDatabase`), `Audio.CacheCleanAudio`
  (por defecto `true`, el comportamiento de siempre). Windows las lee sin problema, **pero al guardar su `deskpanel.json` las escribirá** (`Devonthink` con sus valores por defecto y
  `CacheCleanAudio: true`; las nulas se omiten). Es inofensivo pero el fichero cambiará la primera vez que se guarde desde Windows.
- [ ] `SecretNames.DevonthinkMcpToken` (nuevo, solo Mac).

### Ficheros

- [ ] **`Core/IO/FileNames.cs`**: `Sanitize` sustituye siempre el conjunto reservado de Windows (`<>:"/\|?*`), no el de la plataforma. En Windows el resultado debe ser idéntico.
  **Probar**: que ningún nombre de PDF o resumen existente cambia (títulos con `:` o `?`).
- [ ] **`Core/IO/AtomicFile.cs`**: nueva propiedad estática `StagingDirectory`, nula por defecto. Con ella nula, `WriteAllText` y `ReplaceWith` hacen exactamente lo de antes (temporal `~nombre.guid.tmp`
  junto al destino). Solo el Mac la fija. **Probar**: que guardar configuración, PDF y audio sigue funcionando en carpetas sincronizadas.
- [ ] `Core/Diagnostics/AppLog.cs`: nuevo `PruneOlderThan(TimeSpan)`; Windows no lo llama (sus logs no se podan solos).

### Búsqueda

- [ ] **`DeskPanel.Search/Extraction/TextDecoding.cs`**: `DefaultMaxBytes` sube de **20 MB a 500 MB** (texto plano y RTF), a petición expresa y para ambas plataformas. Un `.txt` se carga entero como
  cadena: **comprobar la memoria** al indexar uno de varios cientos de MB (la extracción corre en paralelo, `ProcessorCount / 2` ficheros a la vez). Los PDF no tienen tope.

### Audio

- [ ] `AudioJobOptions` gana el parámetro opcional `CacheCleanAudio` (por defecto `true`) y `AudioPipeline` solo copia el audio limpio a la caché si es `true`. Sin cambios para Windows.
- `DeskPanel.Audio/AudioTools.cs`: añade `/opt/homebrew/bin` y `/usr/local/bin` a la búsqueda de ffmpeg solo en macOS. Nada que probar en Windows.
- `DeskPanel.Audio.csproj` **no cambia**: Whisper en Mac usa `Whisper.net.Runtime.Metal`, que ya llega como dependencia de `Whisper.net.Runtime`.

### Interfaces nuevas en `Core/Platform`

- [ ] `IPdfRenderer` (nuevo) y `DeskPanel.App/Pdf/PdfRenderer.cs` declara que la implementa (misma firma; sin más cambios). Windows sigue usando WebView2.
- `IShellOpener` (nuevo, con `RevealFile`): solo lo implementa el Mac; Windows no lo usa todavía.

## 3. Proyectos nuevos (no los usa `DeskPanel.App`)

- `DeskPanel.Pdf.Chromium` (`net10.0`, PuppeteerSharp 25.13.1): PDF con el Chrome instalado. Windows **podría** adoptarlo en lugar de WebView2; la paridad medida desde el Mac fue 11 páginas
  en ambos para el mismo artículo (A4 + `conSerifa.css`). Falta la comparación visual.
- `DeskPanel.Mac` (Avalonia 12.1.4, `net10.0`): toda la app del Mac. Su carpeta `Folders/FolderCatalog.cs` es una copia adaptada de `DeskPanel.App/Panel/FolderCatalog.cs`: si cambia la lista
  de carpetas en Windows, hay que cambiarla en los dos sitios.
- Scripts solo para Mac: `tools/install-mac-prerequisites.sh`, `tools/Publish-DeskPanelMac.sh`.

## 4. Qué NO se tocó

`DeskPanel.App` (salvo los envoltorios, `SecretStore.cs`, `SettingsViewModel.Ai.cs`, `WebActions.cs` y la declaración de interfaz de `PdfRenderer`), `DeskPanel.Remote`, `VerificaFicheros`,
y el resto de `DeskPanel.Search`, `DeskPanel.Feeds` y `DeskPanel.Audio`.

## 5. Prueba funcional mínima en Windows

1. Abrir la solución y compilar; arrancar `DeskPanel.exe` con la configuración de siempre (no debe pedir nada nuevo).
2. Feeds: actualizar, resumir una entrada, traducir otra, generar un PDF (nombre con título con `:`), comprobar que el nombre es el de siempre.
3. Audio: procesar un audio corto con la caché configurada (los tres ficheros llegan a la caché, audio limpio incluido).
4. Búsqueda: actualizar un índice con un `.txt` de más de 20 MB; debe entrar sin error y sin disparar la memoria.
5. Ajustes › IA: guardar una clave y volver a abrir (DPAPI); la web: contraseña y arranque del servidor.
6. Guardar la configuración desde Ajustes y mirar el `deskpanel.json`: aparecen `Devonthink` y `CacheCleanAudio`, nada más.

## 6. Soporte nativo para Windows ARM64 (Surface Pro 11)

Hecho desde el Mac, **sin ejecutar nada en Windows ni PowerShell**: se comprobó con `dotnet publish -r win-arm64 -p:EnableWindowsTargeting=true` (los dos `.exe` salen `Aarch64`; la
carpeta `runtimes` solo contiene `win-arm64`) y que el publish x64 sigue dando el mismo resultado que antes (90 MB, `win-x64` y `vulkan`).

- [ ] **`tools/Publish-DeskPanel.ps1`**: nuevo parámetro `-Arquitectura x64|arm64` (por defecto, la del equipo), nombres `dist\DeskPanel-win-<arq>[-autonomo]`, `winget ... --architecture <arq>` en el
  `LEEME-INSTALACION.txt`, y el publish no autónomo pasa `-r`/`-o` como propiedades globales (antes usaba el perfil). **El script no se ha podido ejecutar ni analizar sintácticamente**: probarlo en x64
  (`.\tools\Publish-DeskPanel.ps1`) y en la Surface (`.\tools\Publish-DeskPanel.ps1`, o `-Arquitectura arm64` desde el PC x64) y mirar que el zip trae `DeskPanel.exe` y `VerificaFicheros.exe`.
- [ ] **`DeskPanel.App.csproj`**: nuevo target `PruneForeignWhisperRuntimesArm64` (solo con `win-arm64`; quita todo salvo `runtimes\win-arm64`, incluido `vulkan`, que es solo x64). El de x64 no cambia.
- [ ] `PublishProfiles/DeskPanel-win-arm64.pubxml` (nuevo, para Visual Studio). Ojo: desde Visual Studio el perfil no propaga el runtime a `VerificaFicheros`; el camino soportado es el script.
- [ ] En la Surface: instalar el paquete **arm64** con los runtimes ARM64 y comprobar que ya no pide los x64; que arranca, que el visor/generador de PDF (WebView2) y la web funcionan, y que el audio funciona
  (ffmpeg y `deep-filter` siguen siendo x64 emulados; Whisper corre en CPU).
- [ ] Normalizados a LF `Publish-DeskPanel.ps1`, `DeskPanel.App.csproj` y `DeskPanel-win-x64.pubxml` (estaban en CRLF): `git add --renormalize .` ya lo habría hecho.
